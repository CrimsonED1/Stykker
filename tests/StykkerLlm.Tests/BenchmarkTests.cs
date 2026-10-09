using System.Net;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class BenchmarkTests
{
    private const string Host = "127.0.0.1:8095";

    // Ein Server, der wie llama-server antwortet: 1000 t/s Prompt, 50 t/s Erzeugung, 4 Zeichen je Token
    private static FakeHandler FakeServer(List<string>? bodies = null, int maxContext = 100000)
    {
        var h = new FakeHandler();
        h.Responder = (key, body) =>
        {
            bodies?.Add(key + " " + body);
            if (key == Host + "/tokenize")
            {
                using var d = JsonDocument.Parse(body!);
                int chars = d.RootElement.GetProperty("content").GetString()!.Length;
                return (HttpStatusCode.OK, "{\"tokens\":[" + string.Join(",", Enumerable.Range(0, chars / 4)) + "]}");
            }
            if (key == Host + "/v1/chat/completions")
            {
                using var d = JsonDocument.Parse(body!);
                var content = d.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                int promptN = content.Length / 4 + 10;
                if (promptN > maxContext) return (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"the request exceeds the available context size\"}}");
                bool tools = d.RootElement.TryGetProperty("tools", out _);
                string message = tools
                    ? "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]}"
                    : "{\"role\":\"assistant\",\"content\":\"ok\"}";
                string timings = $"{{\"prompt_n\":{promptN},\"prompt_ms\":{promptN},\"predicted_n\":128,\"predicted_ms\":2560}}";
                return (HttpStatusCode.OK, $"{{\"choices\":[{{\"finish_reason\":\"{(tools ? "tool_calls" : "length")}\",\"message\":{message}}}],\"usage\":{{\"prompt_tokens\":{promptN},\"completion_tokens\":128}},\"timings\":{timings}}}");
            }
            return null;
        };
        return h;
    }

    private static Task<BenchResult> Run(FakeHandler h, BenchOptions o, Action<string>? progress = null, CancellationToken ct = default) =>
        BenchmarkRunner.RunAsync(new HttpClient(h), "http://" + Host, o, new BenchResult { Server = "srv", Model = "m", Started = DateTime.Now }, progress, null, ct);

    [TestMethod]
    public void Filler_IsDeterministicAndExactLength()
    {
        Assert.AreEqual(BenchmarkRunner.Filler(1500, 42), BenchmarkRunner.Filler(1500, 42));
        Assert.AreNotEqual(BenchmarkRunner.Filler(1500, 42), BenchmarkRunner.Filler(1500, 43));
        Assert.AreEqual(1500, BenchmarkRunner.Filler(1500, 42).Length);
    }

    [TestMethod]
    public void UsableSizes_FitIntoTheSlot()
    {
        var o = new BenchOptions { ContextPerSlot = 4096 };
        CollectionAssert.AreEqual(new[] { 1024, 2048 }, o.UsableSizes().ToArray());
        o.ContextPerSlot = 65536;
        Assert.AreEqual(1024, o.UsableSizes().First());
        Assert.AreEqual(32768, o.UsableSizes().Last());       // 65536 + Antwort passt nicht mehr in den Slot
        Assert.AreEqual(0, new BenchOptions { ContextPerSlot = 800 }.UsableSizes().Count());
    }

    [TestMethod]
    public async Task FullRun_ChatContextTool_Numbers()
    {
        var bodies = new List<string>();
        var h = FakeServer(bodies);
        var msgs = new List<string>();
        var r = await Run(h, new BenchOptions { ContextPerSlot = 4096, Slots = 1 }, msgs.Add);
        CollectionAssert.AreEqual(new[] { "Short chat", "Context 1k", "Context 2k", "Tool call" }, r.Steps.Select(s => s.Name).ToArray());
        Assert.IsTrue(r.Steps.All(s => s.Ok), string.Join("; ", r.Steps.Select(s => s.Name + ":" + s.Note)));
        var k1 = r.Steps[1];
        Assert.IsTrue(Math.Abs(k1.PromptTokens - 1024) < 120, "Prompt-Token nahe am Ziel: " + k1.PromptTokens);
        Assert.AreEqual(1000, k1.PromptTps, 1);
        Assert.AreEqual(50, k1.GenTps, 0.01);
        Assert.AreEqual(128, k1.GenTokens);
        Assert.IsTrue(r.Steps[2].PromptTokens > k1.PromptTokens * 1.7);
        Assert.IsTrue(r.Steps[3].Note.StartsWith("ok: get_weather"));
        Assert.IsFalse(r.Cancelled);
        Assert.IsTrue(msgs.Count >= 4);
        // feste Parameter in jeder Anfrage
        foreach (var b in bodies.Where(x => x.StartsWith(Host + "/v1/chat/completions")))
        {
            StringAssert.Contains(b, "\"cache_prompt\":false");
            StringAssert.Contains(b, "\"seed\":42");
            StringAssert.Contains(b, "\"temperature\":0");
        }
    }

    [TestMethod]
    public async Task SameOptionsGiveSameRequests()
    {
        var a = new List<string>(); var b = new List<string>();
        await Run(FakeServer(a), new BenchOptions { ContextPerSlot = 4096, Tool = false });
        await Run(FakeServer(b), new BenchOptions { ContextPerSlot = 4096, Tool = false });
        CollectionAssert.AreEqual(a, b);   // wiederholbar: gleiche Prompts, gleicher Seed
    }

    [TestMethod]
    public async Task ContextError_StopsTheLadder()
    {
        var h = FakeServer(maxContext: 1500);   // der Server nimmt nur ~1500 Token an
        var r = await Run(h, new BenchOptions { ContextPerSlot = 70000, Chat = false, Tool = false, Parallel = false });
        Assert.IsTrue(r.Steps[0].Ok);
        var failed = r.Steps.First(s => !s.Ok);
        StringAssert.Contains(failed.Note, "context");
        Assert.IsTrue(r.Steps.Count < BenchOptions.Ladder.Length);
    }

    [TestMethod]
    public async Task ToolCallMissing_IsReportedNotFailedRun()
    {
        var h = FakeServer();
        var inner = h.Responder!;
        h.Responder = (k, b) => b != null && b.Contains("\"tools\"") && k.EndsWith("/chat/completions")
            ? (HttpStatusCode.OK, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"It is sunny\"}}],\"timings\":{\"prompt_n\":50,\"prompt_ms\":50,\"predicted_n\":5,\"predicted_ms\":50}}")
            : inner(k, b);
        var r = await Run(h, new BenchOptions { ContextPerSlot = 800, Chat = false });
        var tool = r.Steps.Single(s => s.Kind == "tool");
        Assert.IsFalse(tool.Ok);
        StringAssert.Contains(tool.Note, "no tool call");
    }

    [TestMethod]
    public async Task Parallel_OnlyWithSeveralSlots_AggregatesTokens()
    {
        var single = await Run(FakeServer(), new BenchOptions { ContextPerSlot = 800, Chat = false, Tool = false, Slots = 1 });
        Assert.AreEqual(0, single.Steps.Count(s => s.Kind == "parallel"));
        var r = await Run(FakeServer(), new BenchOptions { ContextPerSlot = 2000, Chat = false, Tool = false, Slots = 3 });
        var p = r.Steps.Single(s => s.Kind == "parallel");
        Assert.AreEqual(3, p.Parallel);
        Assert.IsTrue(p.Ok);
        Assert.AreEqual(3 * 128, p.GenTokens);
        Assert.AreEqual(50, p.GenTps, 0.01);                    // je Anfrage
        Assert.IsTrue(p.AggregateTps > 0);
    }

    [TestMethod]
    public async Task ApiKeyIsSentAsBearer_AndErrorsBecomeNotes()
    {
        var h = new FakeHandler();
        var r = await Run(h, new BenchOptions { ContextPerSlot = 800, Tool = false, ApiKey = new SecretValue("sk-test") });
        Assert.IsFalse(r.Steps[0].Ok);                          // nichts erreichbar
        Assert.IsTrue(r.Steps[0].Note.Length > 0);
        Assert.IsTrue(h.AuthHeaders.All(a => a == "Bearer sk-test"));
    }

    [TestMethod]
    public async Task Cancel_KeepsFinishedSteps()
    {
        using var cts = new CancellationTokenSource();
        var h = FakeServer();
        var inner = h.Responder!;
        int calls = 0;
        h.Responder = (k, b) => { if (k.EndsWith("/chat/completions") && ++calls == 2) cts.Cancel(); return inner(k, b); };
        var r = await Run(h, new BenchOptions { ContextPerSlot = 8000, Tool = false }, null, cts.Token);
        Assert.IsTrue(r.Cancelled);
        Assert.IsTrue(r.Steps.Count >= 1 && r.Steps.Count < 4);
    }

    [TestMethod]
    public void Export_MarkdownAndCsv()
    {
        BenchResult Res(string title, double gen) => new()
        {
            Server = title, Model = "m", Quant = "Q4_K_M", Settings = "ctx 4096", Gpu = "RTX", Started = new DateTime(2026, 1, 2, 3, 4, 0), Seed = 42, ContextPerSlot = 4096,
            Steps =
            {
                new BenchStep { Kind = "chat", Name = "Short chat", PromptTps = 800, GenTps = gen, Ok = true },
                new BenchStep { Kind = "context", Name = "Context 8k", PromptTokens = 8000, PromptTps = 1500, GenTps = gen / 2, Ok = true },
                new BenchStep { Kind = "context", Name = "Context 1k", PromptTokens = 1000, PromptTps = 1200, GenTps = gen, Ok = true },
                new BenchStep { Kind = "tool", Name = "Tool call", Ok = true, Note = "ok" },
            },
        };
        var md = BenchmarkExport.Markdown(new[] { Res("A", 50), Res("B | odd", 70) });
        StringAssert.Contains(md, "| Test |");
        StringAssert.Contains(md, "B \\| odd");                   // senkrechter Strich maskiert
        StringAssert.Contains(md, "1500 / 25.0");
        Assert.IsTrue(md.IndexOf("Context 1k") < md.IndexOf("Context 8k"));   // aufsteigend sortiert
        StringAssert.Contains(md, "| Tool call | ok | ok |");
        var csv = BenchmarkExport.Csv(new[] { Res("A", 50) }).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(5, csv.Length);
        Assert.IsTrue(csv[0].StartsWith("Date,Title"));
    }

    [TestMethod]
    public void Library_StoresBenchmarksAndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "slm-bench-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var lib = new Library(path);
            var res = new BenchResult { Server = "srv", Model = "m", Started = DateTime.Now, Steps = { new BenchStep { Name = "Short chat", GenTps = 42, Ok = true } } };
            lib.AddBenchmark(res);
            lib.AddBenchmark(res);   // gleiche Id: nicht doppelt
            Assert.AreEqual(1, lib.Benchmarks.Count);
            lib.Save();
            var back = Library.Load(path);
            Assert.AreEqual(42, back.Benchmarks.Single().Steps[0].GenTps);
            Assert.IsTrue(back.RemoveBenchmark(res.Id));
            Assert.AreEqual(0, back.Benchmarks.Count);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Regression_StaysOnTheResultAndSurvivesTheRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "slm-reg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var lib = new Library(path);
            var prev = Bench("p", "s", 100, new DateTime(2026, 1, 1));
            lib.AddBenchmark(prev);
            var current = Bench("p", "s", 70, new DateTime(2026, 1, 2));
            // So hält es BenchmarkService fest: Rückgang vor dem Speichern am Ergebnis
            current.Regression = BenchmarkRunner.Regression(BenchmarkRunner.PreviousSimilar(lib, current)!, current);
            lib.AddBenchmark(current);
            lib.Save();

            var back = Library.Load(path).Benchmarks.Single(b => b.Id == current.Id);
            Assert.IsNotNull(back.Regression);
            Assert.AreEqual(30.0, back.Regression!.DropPct, 1e-9);
            Assert.AreEqual(100.0, back.Regression.Previous, 1e-9);
            Assert.AreEqual(70.0, back.Regression.Current, 1e-9);
            Assert.IsNull(Library.Load(path).Benchmarks.Single(b => b.Id == prev.Id).Regression);   // der erste Lauf hat nichts zu vergleichen
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Library_ReadsBackAResultWithARegression()
    {
        var path = Path.Combine(Path.GetTempPath(), "slm-regload-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var res = Bench("p", "s", 70, new DateTime(2026, 1, 2));
            res.Regression = new BenchRegression(100, 70, 30);
            var lib0 = new Library(path);
            lib0.AddBenchmark(res);
            lib0.Save();
            Assert.IsTrue(File.Exists(path), "die Bibliothek muss eine Datei schreiben");
            var loaded = Library.Load(path);
            Assert.IsNull(loaded.ReadOnlyReason, "die Datei wurde als beschädigt eingestuft: " + loaded.ReadOnlyReason);
            var back = loaded.Benchmarks;
            Assert.AreEqual(1, back.Count);
            Assert.IsNotNull(back[0].Regression);
            Assert.AreEqual(30.0, back[0].Regression!.DropPct, 1e-9);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Regression_ReachesTheStateAndEverySurfaceText()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-regstate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var settings = AppSettings.Load(paths.SettingsFile);
            using var engine = new MonitorEngine(new FakePlatform(), paths, settings, new HttpClient(new FakeHandler()));
            var dropped = Bench("p", "s", 70, new DateTime(2026, 1, 2));
            dropped.Regression = new BenchRegression(100, 70, 30);
            engine.Library.AddBenchmark(dropped);

            var json = StateJson.WriteText(engine, null, null, 17400, DateTimeOffset.Now);
            StringAssert.Contains(json, "\"regression\"");
            var back = StateSnapshot.Parse(json).Benchmarks.Single().Regression;
            Assert.IsNotNull(back);
            Assert.AreEqual("Possible regression", back!.Title);
            Assert.AreEqual(30.0, back.DropPct, 1e-9);
            Assert.AreEqual(100.0, back.Previous, 1e-9);
            Assert.AreEqual(70.0, back.Current, 1e-9);
            StringAssert.Contains(back.Text, "30%");
            StringAssert.Contains(back.Text, "100.0 -> 70.0 t/s");

            // Ohne Rückgang bleibt das Feld weg (additiv, Schema 1)
            engine.Library.AddBenchmark(Bench("p2", "s", 50, new DateTime(2026, 1, 3)));
            Assert.IsNull(StateSnapshot.Parse(StateJson.WriteText(engine, null, null, 17400, DateTimeOffset.Now))
                .Benchmarks.Single(b => b.Model == "m" && b.Started == new DateTime(2026, 1, 3)).Regression);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Regression_ThresholdComesFromTheSettingsAndZeroSwitchesItOff()
    {
        var prev = Bench("p", "s", 100, new DateTime(2026, 1, 1));
        var dropped = Bench("p", "s", 85, new DateTime(2026, 1, 2));      // -15 %

        // Standard 10 %: wird gemeldet
        var s = new AppSettings();
        Assert.AreEqual(10, s.BenchRegressionPct);
        Assert.IsNotNull(BenchmarkRunner.Regression(prev, dropped, s));

        // Strenger Schwellwert: derselbe Rückgang bleibt unauffällig
        s.BenchRegressionPct = 25;
        Assert.IsNull(BenchmarkRunner.Regression(prev, dropped, s));

        // 0 schaltet die Meldung ganz ab
        s.BenchRegressionPct = 0;
        Assert.IsNull(BenchmarkRunner.Regression(prev, dropped, s));
        Assert.IsNull(BenchmarkRunner.Regression(prev, dropped, 0));
        Assert.IsNotNull(BenchmarkRunner.Regression(prev, dropped, 5));   // sehr empfindlich meldet ihn

        // Die Einstellung überlebt das Speichern und steht im Zustand (Schema 1, additiv)
        var dir = Path.Combine(Path.GetTempPath(), "slm-regset-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var settings = AppSettings.Load(paths.SettingsFile);
            settings.BenchRegressionPct = 25;
            settings.Save();
            Assert.AreEqual(25, AppSettings.Load(paths.SettingsFile).BenchRegressionPct);

            using var engine = new MonitorEngine(new FakePlatform(), paths, settings, new HttpClient(new FakeHandler()));
            Assert.AreEqual(25, StateSnapshot.Parse(StateJson.WriteText(engine, null, null, 17400, DateTimeOffset.Now)).Settings.BenchRegressionPct);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Regression_SettingsActionClampsAndStores()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-regact-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var settings = AppSettings.Load(paths.SettingsFile);
            using var engine = new MonitorEngine(new FakePlatform(), paths, settings, new HttpClient(new FakeHandler()));
            var ctx = new ActionContext { Engine = engine, Launcher = null! };

            var ok = ActionApi.ExecuteAsync(new ActionRequest { Action = "settings.set", Values = { ["benchRegressionPct"] = "25" } }, ctx, null!).GetAwaiter().GetResult();
            Assert.IsTrue(ok.Ok);
            Assert.AreEqual(25, engine.Settings.BenchRegressionPct);
            Assert.AreEqual(25, AppSettings.Load(paths.SettingsFile).BenchRegressionPct);

            ActionApi.ExecuteAsync(new ActionRequest { Action = "settings.set", Values = { ["benchRegressionPct"] = "900" } }, ctx, null!).GetAwaiter().GetResult();
            Assert.AreEqual(100, engine.Settings.BenchRegressionPct);      // 100 % ist das Maximum
            ActionApi.ExecuteAsync(new ActionRequest { Action = "settings.set", Values = { ["benchRegressionPct"] = "-5" } }, ctx, null!).GetAwaiter().GetResult();
            Assert.AreEqual(0, engine.Settings.BenchRegressionPct);        // 0 % schaltet ab
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void MarkdownExport_NamesTheRegression()
    {
        var withDrop = Bench("p", "s", 50, new DateTime(2026, 1, 2));
        withDrop.ProfileName = "A";                       // Title kommt aus dem Profilnamen
        withDrop.Regression = new BenchRegression(80, 50, 37.5);
        var md = BenchmarkExport.Markdown(new[] { withDrop, Bench("p2", "s", 40, new DateTime(2026, 1, 3)) });
        StringAssert.Contains(md, "**Possible regression:**");
        StringAssert.Contains(md, "Generation speed dropped 37.5%");
        StringAssert.Contains(md, "- A:");
        Assert.IsFalse(BenchmarkExport.Markdown(new[] { Bench("p3", "s", 40, new DateTime(2026, 1, 4)) }).Contains("regression"));
    }

    private static BenchResult Bench(string profile, string server, double genTps, DateTime started) => new()
    {
        ProfileKey = profile, ServerKey = server, Server = server, Model = "m", Started = started,
        Steps = { new BenchStep { Kind = "chat", Name = "Short chat", GenTps = genTps, Ok = true } },
    };

    [TestMethod]
    public void Regression_FlagsSignificantDropOnly()
    {
        var prev = Bench("p", "s", 100, new DateTime(2026, 1, 1));
        var dropped = Bench("p", "s", 80, new DateTime(2026, 1, 2));     // -20 %
        var similar = Bench("p", "s", 95, new DateTime(2026, 1, 2));     // -5 %
        var reg = BenchmarkRunner.Regression(prev, dropped);
        Assert.IsNotNull(reg);
        Assert.AreEqual(20.0, reg!.DropPct, 1e-9);
        Assert.AreEqual(100.0, reg.Previous, 1e-9);
        Assert.AreEqual(80.0, reg.Current, 1e-9);
        Assert.IsNull(BenchmarkRunner.Regression(prev, similar));
        Assert.IsNull(BenchmarkRunner.Regression(prev, prev));           // dieselbe Id: kein Vergleich
    }

    [TestMethod]
    public void PreviousSimilar_MatchesProfileThenServerModel()
    {
        var lib = new Library(Path.Combine(Path.GetTempPath(), "slm-prev-" + Guid.NewGuid().ToString("N") + ".json"));
        lib.AddBenchmark(Bench("p", "s", 90, new DateTime(2026, 1, 1)));
        lib.AddBenchmark(Bench("other", "s", 200, new DateTime(2026, 1, 3)));
        lib.AddBenchmark(Bench("p", "s", 95, new DateTime(2026, 1, 2)));
        var current = Bench("p", "s", 80, new DateTime(2026, 1, 4));
        var prev = BenchmarkRunner.PreviousSimilar(lib, current);
        Assert.IsNotNull(prev);
        Assert.AreEqual(95, prev!.Steps[0].GenTps);                      // jüngster Lauf mit gleichem Profil-Schlüssel
    }

    [TestMethod]
    public void Describe_ReportsCpuAndDriverFingerprint()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(BenchmarkRunner.CpuName()));
        var md = BenchmarkExport.Markdown(new[] { new BenchResult { Server = "A", Gpu = "RTX", Driver = "555.1", Cpu = "Test CPU", Seed = 42 } });
        StringAssert.Contains(md, "| CPU | Test CPU |");
        StringAssert.Contains(md, "| GPU driver | 555.1 |");
        var csv = BenchmarkExport.Csv(new[] { new BenchResult { Server = "A", Driver = "555.1", Cpu = "Test CPU" } }).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        StringAssert.StartsWith(csv[0], "Date,Title,Model,Quantization,Settings,Gpu,Driver,Cpu,");
    }
}
