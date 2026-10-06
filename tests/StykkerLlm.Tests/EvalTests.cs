using System.Net;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Tests;

// Test-Suite für Modelle: Bewertung, Suite-Datei, Ablauf gegen einen nachgebildeten Server
[TestClass]
public class EvalTests
{
    private static EvalTask Task(string type, Action<EvalCheck> set) { var t = new EvalTask { Id = "t", Category = "x" }; t.Check.Type = type; set(t.Check); return t; }
    private static readonly IReadOnlyList<EvalToolCall> NoTools = Array.Empty<EvalToolCall>();

    [TestMethod]
    public async Task Grader_ExactNumberRegexJson()
    {
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(Task("exact", c => c.Expected.Add("Carl")), "<think>hmm</think>**Carl.**", NoTools, null, default)).Score);
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(Task("exact", c => c.Expected.Add("Carl")), "Ben", NoTools, null, default)).Score);
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(Task("number", c => c.Expected.Add("155")), "The trip takes 155 minutes.", NoTools, null, default)).Score);
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(Task("number", c => c.Expected.Add("1815")), "1,815", NoTools, null, default)).Score);
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(Task("regex", c => { c.Pattern = "^blue$"; c.IgnoreCase = false; }), "blue", NoTools, null, default)).Score);
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(Task("regex", c => { c.Pattern = "^blue$"; c.IgnoreCase = false; }), "Blue", NoTools, null, default)).Score);
        var json = Task("json", c => { c.Expected.AddRange(new[] { "name", "born" }); c.Values["born"] = "1815"; });
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(json, "```json\n{\"name\":\"Ada\",\"born\":1815}\n```", NoTools, null, default)).Score);
        Assert.AreEqual(0.5, (await EvalGrader.GradeAsync(json, "Sure! {\"name\":\"Ada\",\"born\":1815}", NoTools, null, default)).Score);
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(json, "{\"name\":\"Ada\",\"born\":1816}", NoTools, null, default)).Score);
    }

    [TestMethod]
    public async Task Grader_ReviewFindings()
    {
        // **Carl**. (Punkt hinter dem Fettdruck)
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(Task("exact", c => c.Expected.Add("Carl")), "**Carl**.", NoTools, null, default)).Score);
        // abgeschnittener Denkteil: keine Antwort, Zahlen daraus zählen nicht
        Assert.AreEqual("", EvalGrader.StripThinking("<think>first 155 then"));
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(Task("number", c => c.Expected.Add("155")), "<think>maybe 155", NoTools, null, default)).Score);
        // Rechenweg vor dem Ergebnis: die letzte Zahl zählt
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(Task("number", c => c.Expected.Add("371")), "17*23 = 391, +12 = 403, -32 = 371", NoTools, null, default)).Score);
        // "42" ist nicht "4"
        var four = Task("notool", c => c.Pattern = @"(?<![\d.])4(?!\d|\.\d)");
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(four, "42", NoTools, null, default)).Score);
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(four, "2 + 2 = 4.", NoTools, null, default)).Score);
        // JSON mit falschem Wert und Text drumherum: 0, nicht 0,5; Zahl als String: 0,5
        var json = Task("json", c => c.Values["born"] = "1815");
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(json, "Here: {\"born\":1816}", NoTools, null, default)).Score);
        Assert.AreEqual(0.5, (await EvalGrader.GradeAsync(json, "{\"born\":\"1815\"}", NoTools, null, default)).Score);
        // regex ohne Multiline: ^blue$ passt nicht auf eine Zeile mitten im Text
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(Task("regex", c => c.Pattern = "^blue$"), "The sky is\nblue\nmostly", NoTools, null, default)).Score);
        // ungültiges Pattern: Bewerter-Fehler, keine Ausnahme
        StringAssert.StartsWith((await EvalGrader.GradeAsync(Task("regex", c => c.Pattern = "(["), "x", NoTools, null, default)).Note, "grader error");
        // ```Python (groß) wird erkannt; mehrere Python-Blöcke werden verkettet
        Assert.AreEqual("a=1\n\n\nb=2\n", EvalGrader.ExtractCode("```Python\na=1\n```\ntext\n```py\nb=2\n```"));
    }

    [TestMethod]
    public async Task Python_ExitBeforeTests_AndOutputFlood()
    {
        var py = PythonRunner.Find();
        if (py == null) Assert.Inconclusive("no Python on this machine");
        var t = Task("python", c => c.Tests = "assert False");
        var early = await EvalGrader.GradeAsync(t, "```python\nimport sys\nsys.exit(0)\n```", NoTools, py, default);
        Assert.AreEqual(0, early.Score);
        StringAssert.Contains(early.Note, "before the tests");
        var flood = Task("python", c => { c.Tests = ""; c.TimeoutSec = 20; });
        var r = await EvalGrader.GradeAsync(flood, "```python\nwhile True: print('x' * 1000)\n```", NoTools, py, default);
        Assert.AreEqual(0, r.Score);
        StringAssert.Contains(r.Note, "flood");
    }

    [TestMethod]
    public async Task Runner_ToolChain_FeedsResultsBack()
    {
        var suite = new EvalSuite { Name = "t", Tasks =
        {
            new EvalTask
            {
                Id = "chain", Category = "tools", Prompt = "weather?",
                Tools = System.Text.Json.JsonDocument.Parse("[{\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"parameters\":{}}}]").RootElement,
                ToolResults = { ["get_weather"] = "{\"temp_c\": -3}" },
                Check = new EvalCheck { Type = "toolchain", Expected = { "get_weather" }, Pattern = "26[.,]6" },
            },
        } };
        var bodies = new List<string>();
        var h = new FakeHandler();
        h.Responder = (key, body) =>
        {
            if (!key.EndsWith("/v1/chat/completions")) return null;
            bodies.Add(body ?? "");
            return bodies.Count == 1
                ? (HttpStatusCode.OK, "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Oslo\\\"}\"}}]}}]}")
                : (HttpStatusCode.OK, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"It is 26.6 °F.\"}}]}");
        };
        var run = await EvalRunner.RunAsync(new HttpClient(h), "http://127.0.0.1:9", suite, new EvalOptions(), default);
        Assert.AreEqual(2, bodies.Count, "second round after the tool result");
        StringAssert.Contains(bodies[1], "\"tool_call_id\":\"c1\"");
        StringAssert.Contains(bodies[1], "temp_c");
        Assert.IsTrue(run.Tasks[0].Passed, run.Tasks[0].Note);
        Assert.AreEqual(2, run.Tasks[0].Rounds);
    }

    [TestMethod]
    public void HardSuite_IsValid_AndPlacesAllNeedles()
    {
        var s = EvalSuites.BuiltIn("hard");
        Assert.IsTrue(s.Tasks.Count >= 25);
        Assert.AreEqual(s.Tasks.Count, s.Tasks.Select(t => t.Id).Distinct().Count());
        var known = new[] { "exact", "number", "regex", "contains", "json", "python", "tool", "notool", "pycheck", "toolchain" };
        foreach (var t in s.Tasks) Assert.IsTrue(known.Contains(t.Check.Type), t.Id);
        var multi = s.Tasks.First(t => t.Needles.Count == 2);
        var p = EvalRunner.BuildPrompt(multi, s.Seed);
        foreach (var n in multi.Needles) StringAssert.Contains(p, n);
        Assert.IsTrue(p.IndexOf(multi.Needles[0], StringComparison.Ordinal) < p.IndexOf(multi.Needles[1], StringComparison.Ordinal));
        Assert.AreEqual(4, EvalSuites.All(null).Count);   // basic, hard, creative, agent
        Assert.IsTrue(EvalSuites.BuiltIn("creative").Tasks.All(t => t.Check.Type == "pycheck" && t.Category == "creative"));
    }

    [TestMethod]
    public async Task Runner_PyCheck_RunsWithoutCodeConsent()
    {
        var py = PythonRunner.Find();
        if (py == null) Assert.Inconclusive("no Python on this machine");
        var suite = new EvalSuite { Name = "t", Tasks =
        {
            new EvalTask { Id = "c", Category = "creative", Prompt = "x", Check = new EvalCheck { Type = "pycheck", Tests = "assert answer == '4'" } },
            new EvalTask { Id = "p", Category = "coding", Prompt = "x", Check = new EvalCheck { Type = "python", Tests = "" } },
        } };
        var h = new FakeHandler();
        h.Responder = (key, _) => key.EndsWith("/v1/chat/completions") ? (HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"4\"}}]}") : null;
        // Python nur für die Antwortprüfung, kein Modell-Code: pycheck läuft, python-Aufgabe wird übersprungen
        var run = await EvalRunner.RunAsync(new HttpClient(h), "http://127.0.0.1:9", suite, new EvalOptions { PythonChecks = py }, default);
        Assert.IsTrue(run.Tasks[0].Passed, run.Tasks[0].Note);
        Assert.IsTrue(run.Tasks[1].Skipped);
    }

    [TestMethod]
    public void Overview_GroupsByModelFile_MeanAndSpread()
    {
        EvalRun Run(string file, double score, int ver = 2) => new()
        {
            Suite = "basic", SuiteVersion = ver, ModelFile = file, Model = "srv-" + file, Started = DateTime.Now,
            Tasks = { new EvalTaskResult { Id = "a", Category = "coding", Score = score, GenTokens = 10, GenTps = 40 } },
        };
        var runs = new[] { Run("a.gguf", 1), Run("A.gguf", 0), Run("b.gguf", 1), Run("a.gguf", 1, ver: 1) };
        var s = EvalOverview.Summarize(runs, "basic", 2);
        Assert.AreEqual(2, s.Count);
        Assert.AreEqual("b.gguf", s[0].ModelFile);            // beste Note zuerst
        var a = s.Single(x => x.ModelFile.Equals("a.gguf", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(2, a.Runs);                            // Groß/klein egal, andere Suite-Version zählt nicht
        Assert.AreEqual(50, a.Total, 1e-9);
        Assert.AreEqual(70.71, a.TotalSd, 0.01);
        Assert.AreEqual(40, a.GenTps, 1e-9);
    }

    [TestMethod]
    public async Task Grader_Tools()
    {
        var t = Task("tool", c => { c.Tool = "get_weather"; c.Args["city"] = "Hamburg"; });
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(t, "", new[] { new EvalToolCall("get_weather", "{\"city\":\"Hamburg, DE\"}") }, null, default)).Score);
        Assert.AreEqual(0.5, (await EvalGrader.GradeAsync(t, "", new[] { new EvalToolCall("get_weather", "{\"city\":\"Berlin\"}") }, null, default)).Score);
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(t, "It is sunny.", NoTools, null, default)).Score);
        var none = Task("notool", c => c.Expected.Add("4"));
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(none, "2 + 2 = 4", NoTools, null, default)).Score);
        Assert.AreEqual(0, (await EvalGrader.GradeAsync(none, "", new[] { new EvalToolCall("search_web", "{}") }, null, default)).Score);
    }

    [TestMethod]
    public async Task Grader_Python_RunsCodeAgainstTests()
    {
        var py = PythonRunner.Find();
        if (py == null) Assert.Inconclusive("no Python on this machine");
        var t = Task("python", c => c.Tests = "assert add(2, 3) == 5");
        Assert.AreEqual(1, (await EvalGrader.GradeAsync(t, "Here:\n```python\ndef add(a, b):\n    return a + b\n```", NoTools, py, default)).Score);
        var bad = await EvalGrader.GradeAsync(t, "```python\ndef add(a, b):\n    return a - b\n```", NoTools, py, default);
        Assert.AreEqual(0, bad.Score);
        StringAssert.Contains(bad.Note, "AssertionError");
        var slow = Task("python", c => { c.Tests = "while True: pass"; c.TimeoutSec = 1; });
        StringAssert.Contains((await EvalGrader.GradeAsync(slow, "```python\nx = 1\n```", NoTools, py, default)).Note, "timeout");
    }

    [TestMethod]
    public void BuiltInSuite_IsValid()
    {
        var s = EvalSuites.BuiltIn();
        Assert.IsTrue(s.Tasks.Count >= 20);
        Assert.AreEqual(s.Tasks.Count, s.Tasks.Select(t => t.Id).Distinct().Count(), "ids unique");
        var known = new[] { "exact", "number", "regex", "contains", "json", "python", "tool", "notool" };
        foreach (var t in s.Tasks) Assert.IsTrue(known.Contains(t.Check.Type), t.Id);
        var ctx = s.Tasks.First(t => t.FillerTokens > 0);
        StringAssert.Contains(EvalRunner.BuildPrompt(ctx, s.Seed), ctx.Needle!);
    }

    [TestMethod]
    public async Task Runner_ScoresAndSkips()
    {
        var suite = new EvalSuite { Name = "t", Tasks =
        {
            new EvalTask { Id = "a", Category = "reasoning", Prompt = "2+2?", Check = new EvalCheck { Type = "number", Expected = { "4" } } },
            new EvalTask { Id = "b", Category = "reasoning", Prompt = "3+3?", Check = new EvalCheck { Type = "number", Expected = { "6" } } },
            new EvalTask { Id = "c", Category = "coding", Prompt = "code", Check = new EvalCheck { Type = "python", Tests = "" } },
            new EvalTask { Id = "d", Category = "context", Prompt = "q", FillerTokens = 50000, Needle = "N", Check = new EvalCheck { Type = "exact", Expected = { "N" } } },
        } };
        var h = new FakeHandler();
        h.Responder = (key, body) => key.EndsWith("/v1/chat/completions")
            ? (HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"<think>x</think>4\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5},\"timings\":{\"predicted_per_second\":50}}")
            : null;
        var run = await EvalRunner.RunAsync(new HttpClient(h), "http://127.0.0.1:9", suite, new EvalOptions { ContextLimit = 8192 }, default);
        Assert.AreEqual(4, run.Tasks.Count);
        Assert.IsTrue(run.Tasks[0].Passed);
        Assert.IsFalse(run.Tasks[1].Passed);
        Assert.IsTrue(run.Tasks[2].Skipped, "no Python allowed");
        Assert.IsTrue(run.Tasks[3].Skipped, "context too small");
        Assert.AreEqual(50, run.Total, 1e-9);   // nur reasoning bewertet: 1 von 2
        Assert.AreEqual(50, run.Tasks[0].GenTps, 1e-9);
        // Serverfehler zählen nicht in die Note
        run.Tasks.Add(new EvalTaskResult { Id = "e", Category = "reasoning", Error = true });
        Assert.AreEqual(50, run.Total, 1e-9);
        StringAssert.Contains(EvalExport.Markdown(new[] { run }), "| reasoning | 50 (1/2) |");
    }

    // Welche Maschine hat gerechnet? Für den Vergleich zweier Rechner entscheidend: bei einem entfernten
    // Ziel ist das der Host aus der URL (nicht der Rechner, von dem aus gestartet wurde).
    [TestMethod]
    public void MachineOf_RemoteHostIsTakenFromTheUrl_LocalMeansThisPc()
    {
        Assert.AreEqual("192.168.178.157", EvalTarget.MachineOf("http://192.168.178.157:8081"));
        Assert.AreEqual("alexpc", EvalTarget.MachineOf("http://alexpc:8081/v1"));
        Assert.AreEqual(Environment.MachineName, EvalTarget.MachineOf("http://127.0.0.1:8081"));
        Assert.AreEqual(Environment.MachineName, EvalTarget.MachineOf("http://localhost:8081"));
        Assert.AreEqual(Environment.MachineName, EvalTarget.MachineOf("keine-url"));
    }

    // Ein entfernter Server nennt Modell, ctx und Slots nur in /props – ohne sie bliebe der Lauf nicht zuordenbar.
    [TestMethod]
    public async Task ProbeAsync_ReadsModelAndContextFromTheTarget()
    {
        var h = new FakeHandler();
        // So meldet sich llama-server: n_ctx steht unter default_generation_settings
        h.Responder = (key, body) => key.EndsWith("/props")
            ? (HttpStatusCode.OK, @"{""build_info"":""b"",""model_path"":""C:\\modelle\\bonsai.gguf"",""model_alias"":""bonsai"",""total_slots"":2,""model_ftype"":""Q4_K_M"",""default_generation_settings"":{""n_ctx"":131072}}")
            : null;
        var info = await EvalTarget.ProbeAsync(new HttpClient(h), "http://192.168.178.157:8081");
        Assert.AreEqual("192.168.178.157", info.Machine);
        Assert.AreEqual("bonsai", info.Model);
        Assert.AreEqual("bonsai.gguf", info.ModelFile);
        StringAssert.Contains(info.Settings, "ctx 131072");
        StringAssert.Contains(info.Settings, "slots 2");
        Assert.AreEqual("Q4_K_M", info.Quant);
    }

    // Ein alter oder nicht erreichbarer Server darf den Lauf nicht verhindern: die Felder bleiben dann leer.
    [TestMethod]
    public async Task ProbeAsync_SurvivesAServerWithoutProps()
    {
        var h = new FakeHandler();       // antwortet auf nichts
        var info = await EvalTarget.ProbeAsync(new HttpClient(h), "http://192.168.178.157:8081");
        Assert.AreEqual("192.168.178.157", info.Machine);
        Assert.AreEqual("", info.ModelFile);
        Assert.AreEqual("", info.Settings);
    }

    // Derselbe Modellname auf zwei Rechnern ist ein Vergleich, keine Verschmelzung zu einer Zeile.
    [TestMethod]
    public void Summarize_KeepsTheSameModelOnTwoMachinesApart()
    {
        var suite = new EvalSuite { Name = "basic", Version = 2 };
        EvalRun Run(string machine, double total)
        {
            var r = new EvalRun
            {
                Started = DateTime.Now, Suite = suite.Name, SuiteVersion = suite.Version, Machine = machine,
                Model = "bonsai", ModelFile = "bonsai.gguf", Server = machine,
                Tasks = { new EvalTaskResult { Id = "a", Category = "reasoning", Score = total / 100.0 } },
            };
            return r;
        }
        var sums = EvalOverview.Summarize(new[] { Run("RTX-PC", 80), Run("ALEXPC", 40) }, "basic", 2);
        Assert.AreEqual(2, sums.Count, "zwei Rechner = zwei Zeilen");
        Assert.AreEqual(80.0, sums.First(s => s.Machine == "RTX-PC").Total, 1e-9);
        Assert.AreEqual(40.0, sums.First(s => s.Machine == "ALEXPC").Total, 1e-9);
        // Die Rangliste (Web, Fenster) trennt sie ebenso
        Assert.AreEqual(2, EvalOverview.Rank(new[] { Run("RTX-PC", 80), Run("ALEXPC", 40) }, "basic").Count);
    }

    // Ältere Läufe ohne Maschinenangabe bleiben benutzbar und landen wie bisher in einer Gruppe.
    [TestMethod]
    public void Summarize_KeepsOlderRunsWithoutMachineTogether()
    {
        var suite = new EvalSuite { Name = "basic", Version = 2 };
        EvalRun Run(string machine) => new()
        {
            Started = DateTime.Now, Suite = "basic", SuiteVersion = 2, Machine = machine, Model = "m", ModelFile = "m.gguf", Server = "t",
            Tasks = { new EvalTaskResult { Id = "a", Category = "reasoning", Score = 0.8 } },
        };
        Assert.AreEqual(1, EvalOverview.Summarize(new[] { Run(""), Run("") }, "basic", 2).Count);
        Assert.AreEqual(2, EvalOverview.Summarize(new[] { Run(""), Run("ALEXPC") }, "basic", 2).Count);
    }
}
