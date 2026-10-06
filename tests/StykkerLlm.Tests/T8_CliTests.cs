using System.Text.Json;
using StykkerLlm.Cli;
using StykkerLlm.Cli.Tui;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class T8_CliArgsTests
{
    [TestMethod]
    public void CommandWordsAndOptions_InAnyOrder()
    {
        var (a, err) = CliArgs.Parse(new[] { "--data-dir", "D:\\x", "stop", "qwen", "3", "--yes" });
        Assert.IsNull(err);
        Assert.AreEqual("stop", a.Command);
        CollectionAssert.AreEqual(new[] { "qwen", "3" }, a.Words);
        Assert.AreEqual("D:\\x", a.DataDir);
        Assert.IsTrue(a.Yes);
    }

    [TestMethod]
    public void Watch_NumberIsOptional()
    {
        Assert.AreEqual(1, CliArgs.Parse(new[] { "status", "--watch" }).Args.WatchSeconds);
        Assert.AreEqual(5, CliArgs.Parse(new[] { "status", "--watch", "5", "--json" }).Args.WatchSeconds);
        Assert.IsNotNull(CliArgs.Parse(new[] { "status", "--watch", "0" }).Error);
    }

    [TestMethod]
    public void UnknownOptionAndMissingValues_AreErrors()
    {
        Assert.IsNotNull(CliArgs.Parse(new[] { "--bogus" }).Error);
        Assert.IsNotNull(CliArgs.Parse(new[] { "--data-dir" }).Error);
        Assert.IsNotNull(CliArgs.Parse(new[] { "--color", "pink" }).Error);
        Assert.IsNotNull(CliArgs.Parse(new[] { "--interval", "10" }).Error);
        Assert.AreEqual(ColorMode.Ansi16, CliArgs.Parse(new[] { "--color", "16" }).Args.Color);
    }

    [TestMethod]
    public void NegativeNumberIsAWord_NotAnOption()
    {
        var (a, err) = CliArgs.Parse(new[] { "unload", "ollama", "-1" });
        Assert.IsNull(err);
        CollectionAssert.AreEqual(new[] { "ollama", "-1" }, a.Words);
    }
}

[TestClass]
public class T8_FindTests
{
    private static readonly string[] Names = { "qwen3-8b", "qwen3-coder-30b", "gemma-3-12b" };

    [TestMethod]
    public void ExactBeatsPrefixBeatsContains()
    {
        Assert.AreEqual("qwen3-8b", Commands.Find(Names, n => n, "QWEN3-8B", out _));
        Assert.AreEqual("gemma-3-12b", Commands.Find(Names, n => n, "gem", out _));
        Assert.AreEqual("qwen3-coder-30b", Commands.Find(Names, n => n, "coder", out _));
    }

    [TestMethod]
    public void TwoHitsOnTheSameLevel_AreAmbiguous_NothingIsGuessed()
    {
        Assert.IsNull(Commands.Find(Names, n => n, "qwen", out var amb));
        Assert.AreEqual(2, amb.Count);
        Assert.IsNull(Commands.Find(Names, n => n, "llama", out amb));
        Assert.AreEqual(0, amb.Count);
    }
}

[TestClass]
public class T8_StatusJsonTests
{
    [TestMethod]
    public async Task SimulatorStatus_HasSchemaAndAllServers()
    {
        using var sim = new SimHost(SimServerSpec.Defaults(), seed: 1);
        sim.World.Start();
        await sim.Engine.TickAsync();
        await sim.Engine.TickAsync();
        using var ms = new MemoryStream();
        StatusJson.Write(ms, sim.Engine, limited: false, indented: false, DateTimeOffset.Now);
        using var doc = JsonDocument.Parse(ms.ToArray());
        var root = doc.RootElement;
        Assert.AreEqual(StatusJson.Schema, root.GetProperty("schema").GetInt32());
        Assert.IsTrue(root.GetProperty("simulated").GetBoolean());
        Assert.AreEqual(JsonValueKind.Object, root.GetProperty("gpu").ValueKind);
        var servers = root.GetProperty("servers");
        Assert.AreEqual(sim.Engine.Servers.Count, servers.GetArrayLength());
        Assert.IsTrue(servers.GetArrayLength() >= 3);
        foreach (var s in servers.EnumerateArray())
        {
            foreach (var field in new[] { "name", "key", "url", "backend", "state", "online", "tps", "slots", "models", "clients" })
                Assert.IsTrue(s.TryGetProperty(field, out _), "missing " + field);
            CollectionAssert.Contains(new[] { "offline", "loading", "sleeping", "idle", "busy" }, s.GetProperty("state").GetString());
        }
        // eine Zeile ohne Einrückung (für --watch / NDJSON)
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(ms.ToArray()).Contains('\n'));
    }
}

[TestClass]
public class T8_ReadOnlyEngineTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "slm-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [TestMethod]
    public void LibrarySnapshot_ReadsProfiles_ButNeverWrites()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "library.json");
        File.WriteAllText(path, "{\"Profiles\":[{\"Name\":\"p\",\"Program\":\"llama-server\",\"Args\":[\"-m\",\"a.gguf\"]}]}");
        var before = File.ReadAllText(path);
        var lib = Library.LoadSnapshot(path);
        Assert.AreEqual(1, lib.Profiles.Count);
        lib.MarkStarted(lib.Profiles[0].Id, DateTime.Now);
        lib.Save();
        Assert.AreEqual(before, File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public void LibrarySnapshot_BrokenFileIsLeftAlone()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "library.json");
        File.WriteAllText(path, "{ not json");
        var lib = Library.LoadSnapshot(path);
        Assert.AreEqual(0, lib.Profiles.Count);
        Assert.IsTrue(File.Exists(path), "a read-only view must not move a broken file aside");
        Assert.AreEqual(1, Directory.GetFiles(dir).Length);
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public async Task ReadOnlyEngine_WritesNoCsv_AndRefusesRecording()
    {
        var dir = NewDir();
        var paths = new AppPaths(dir);
        var p = new FakePlatform();
        using (var e = new MonitorEngine(p, paths, new AppSettings(), new HttpClient(new FakeHandler()), readOnly: true))
        {
            await e.TickAsync();
            Assert.IsTrue(e.ReadOnly);
            Assert.IsFalse(e.Csv.Enabled);
            Assert.IsNull(await e.StartRecordingAllAsync());
        }
        Assert.IsFalse(File.Exists(paths.DefaultCsv));
        Assert.IsFalse(File.Exists(paths.LibraryFile));
        Directory.Delete(dir, true);
    }
}

[TestClass]
[DoNotParallelize]   // leitet Console.Out um
public class T8_ShowTests
{
    private const string Lib = """
        {"Version":1,"Profiles":[{"Name":"coder","Program":"llama-server","Args":["-m","b.gguf"]}],"Benchmarks":[],
         "History":[{"Key":"a1b2c3d4e5f60718","Program":"llama-server","Args":["-m","a.gguf","-c","8192","--api-key","***"],
           "Name":"qwen","Ctx":8192,"HasSecrets":true,"Env":{"GGML_X":"1"},"Runs":2,"TotalSeconds":75}]}
        """;

    private static (int Code, string Text) Run(string dir, params string[] argv)
    {
        var (a, err) = CliArgs.Parse(argv.Concat(new[] { "--data-dir", dir }).ToArray());
        Assert.IsNull(err);
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { return (Commands.Show(a), sw.ToString()); }
        finally { Console.SetOut(old); }
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "slm-show-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "library.json"), Lib);
        return d;
    }

    [TestMethod]
    public void HistoryById_ShowsCommandParamsAndRuns_SecretStaysHidden()
    {
        var d = NewDir();
        var before = File.ReadAllText(Path.Combine(d, "library.json"));
        var (code, text) = Run(d, "show", "a1b2");
        Assert.AreEqual(Commands.Ok, code);
        StringAssert.Contains(text, "llama-server -m a.gguf -c 8192 --api-key ***");
        StringAssert.Contains(text, Strings.EnvRow("GGML_X"));   // dieselbe Bezeichnung wie im Fenster und im Web
        StringAssert.Contains(text, "1 min 15 s");
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(d, "library.json")), "show must not write");
        Directory.Delete(d, true);
    }

    [TestMethod]
    public void Json_IsOneObject_AndProfilesWorkToo()
    {
        var d = NewDir();
        var (code, text) = Run(d, "show", "coder", "--json");
        Assert.AreEqual(Commands.Ok, code);
        using var doc = JsonDocument.Parse(text);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.AreEqual("profile", doc.RootElement.GetProperty("kind").GetString());
        Assert.AreEqual(Commands.NotFound, Run(d, "show", "nothing-like-this").Code);
        Assert.AreEqual(Commands.Usage, Run(d, "show").Code);
        Directory.Delete(d, true);
    }
}

[TestClass]
public class T8_AnsiTests
{
    private const string Red = "\u001b[31m", Reset = "\u001b[0m";

    [TestMethod]
    public void Width_IgnoresColorCodes()
    {
        Assert.AreEqual(5, StykkerLlm.Cli.Tui.Ansi.Width(Red + "hello" + Reset));
        Assert.AreEqual("hello", StykkerLlm.Cli.Tui.Ansi.Strip(Red + "hel" + Reset + "lo"));
    }

    [TestMethod]
    public void Fit_CutsVisibleCharsKeepsCodes_AndPads()
    {
        var s = StykkerLlm.Cli.Tui.Ansi.Fit(Red + "abcdefgh" + Reset, 5);
        Assert.AreEqual(5, StykkerLlm.Cli.Tui.Ansi.Width(s));
        Assert.AreEqual("abcd…", StykkerLlm.Cli.Tui.Ansi.Strip(s));
        Assert.IsTrue(s.EndsWith(Reset));
        Assert.AreEqual(8, StykkerLlm.Cli.Tui.Ansi.Width(StykkerLlm.Cli.Tui.Ansi.Fit("ab", 8, pad: true)));
    }

    [TestMethod]
    public void Wrap_PrefersSpaces_AndCarriesTheColor()
    {
        var lines = StykkerLlm.Cli.Tui.Ansi.Wrap(Red + "alpha beta gamma delta" + Reset, 12);
        Assert.IsTrue(lines.All(l => StykkerLlm.Cli.Tui.Ansi.Width(l) <= 12));
        CollectionAssert.AreEqual(new[] { "alpha beta", "gamma delta" }, lines.Select(StykkerLlm.Cli.Tui.Ansi.Strip).ToList());
        Assert.IsTrue(lines[1].StartsWith(Red), "the color continues on the next line");
        // ohne Leerzeichen: hart umbrechen
        Assert.AreEqual(3, StykkerLlm.Cli.Tui.Ansi.Wrap(new string('x', 25), 10).Count);
    }

    [TestMethod]
    public void AsciiMode_LeavesOnlyAscii()
    {
        var s = StykkerLlm.Cli.Tui.TuiApp.ToAscii("╭─ ◆ ok · 5 °C – ▕██▌·▏ … ›");
        Assert.IsTrue(s.All(c => c < 128), s);
    }

    [TestMethod]
    public void Palette_FallsBackTo256And16()
    {
        var c = new StykkerLlm.Cli.Tui.Rgb(255, 0, 0);
        Assert.AreEqual(196, StykkerLlm.Cli.Tui.Paint.To256(c));
        Assert.AreEqual(31, StykkerLlm.Cli.Tui.Paint.To16(c));   // (205,49,49) liegt näher als das helle Rot 91
        Assert.AreEqual("", new StykkerLlm.Cli.Tui.Paint(StykkerLlm.Cli.Tui.TuiTheme.DeepSea, ColorMode.None).Fg(c));
    }
}

[TestClass]
[DoNotParallelize]   // die Oberfläche leitet Console.Out während des Laufs um
public class T8_InteractiveTests
{
    private static async Task<(VirtualTerminal Term, StykkerLlm.Cli.Tui.TuiApp App)> Run(string keys, int w = 110, int h = 34)
    {
        var (a, err) = CliArgs.Parse(new[] { "--sim", "--interval", "250", "--color", "none" });
        Assert.IsNull(err);
        var prompt = new StykkerLlm.Cli.Tui.TuiPrompt();
        using var s = new Session(a, SessionMode.Auto, prompt);
        var term = new VirtualTerminal(w, h, keys);
        var app = new StykkerLlm.Cli.Tui.TuiApp(term, s, prompt, a);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await app.RunAsync(cts.Token);
        return (term, app);
    }

    private static string Text(VirtualTerminal t) => string.Join("\n", t.Screen.Select(StykkerLlm.Cli.Tui.Ansi.Strip));

    [TestMethod]
    public async Task Start_ShowsHeaderServersAndInputBox()
    {
        var (t, _) = await Run("{wait 1500}");
        var text = Text(t);
        Assert.AreEqual(34, t.Screen.Length);
        StringAssert.Contains(text, "StykkerLLM");
        StringAssert.Contains(text, "SIMULATION");
        StringAssert.Contains(text, "qwen3-8b");
        StringAssert.Contains(t.Screen[^3], "│ >");
        Assert.IsTrue(t.Screen.All(l => StykkerLlm.Cli.Tui.Ansi.Width(l) <= 110), "no line is wider than the terminal");
    }

    [TestMethod]
    public async Task Slash_ShowsMatchingSuggestions()
    {
        var (t, _) = await Run("{wait 800}/st");
        var text = Text(t);
        StringAssert.Contains(text, "/status");
        StringAssert.Contains(text, "/start <name|id>");
        StringAssert.Contains(text, "/stop <name|port>");
        Assert.IsFalse(text.Contains("/history  "), "only matching commands");
    }

    [TestMethod]
    public async Task HistoryAndShow_RunInside_WithoutLeaving()
    {
        var (_, app) = await Run("{wait 1500}/history{enter}{wait 300}/show qwen{enter}{wait 300}");
        var output = app.OutputText;
        StringAssert.Contains(output, "> /history");
        StringAssert.Contains(output, Strings.ColLastSeen);
        StringAssert.Contains(output, "Command line (secrets are hidden)");
        StringAssert.Contains(output, "llama-server");
    }

    [TestMethod]
    public async Task Stop_AsksInTheInputLine_NoMeansNothingHappens()
    {
        var (t, app) = await Run("{wait 1500}/stop 8082{enter}{wait 400}n{enter}{wait 400}");
        StringAssert.Contains(app.OutputText, "Stop server \"gemma-3-12b\"?");
        StringAssert.Contains(app.OutputText, "→ no");
        Assert.IsFalse(app.OutputText.Contains("stopped gemma"));
        StringAssert.Contains(Text(t), "gemma-3-12b");
    }

    [TestMethod]
    public async Task Stop_Yes_StopsTheSimulatedServer()
    {
        var (_, app) = await Run("{wait 1500}/stop 8082{enter}{wait 400}y{enter}{wait 600}");
        StringAssert.Contains(app.OutputText, "stopped gemma-3-12b");
    }

    [TestMethod]
    public async Task UnknownCommand_IsReported_AndThemeSwitches()
    {
        var (t, app) = await Run("{wait 800}/frobnicate{enter}/theme phosphor{enter}{wait 200}");
        StringAssert.Contains(app.OutputText, "unknown command 'frobnicate'");
        StringAssert.Contains(Text(t), "Phosphor");
    }
}
