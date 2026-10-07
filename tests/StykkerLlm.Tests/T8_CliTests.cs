using StykkerLlm.Cli;
using StykkerLlm.Cli.Tui;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// stykker: Befehle ohne Optionen, die Anzeige im Terminal (Tasten statt Befehle), Ausgabe-Hilfen.
[TestClass]
public class T8_CliArgsTests
{
    [TestMethod]
    public void Commands_AreWords_TheRestIsText()
    {
        var (a, err) = CliArgs.Parse(new[] { "bugreport", "window", "stays", "black" });
        Assert.IsNull(err);
        Assert.AreEqual("bugreport", a.Command);
        CollectionAssert.AreEqual(new[] { "window", "stays", "black" }, a.Words);
        Assert.AreEqual("", CliArgs.Parse(Array.Empty<string>()).Args.Command);
    }

    [TestMethod]
    public void UserOptions_AreRefused_WithAHintToTheWeb()
    {
        foreach (var opt in new[] { "--json", "--yes", "--watch", "--color" })
        {
            var (_, err) = CliArgs.Parse(new[] { "status", opt });
            Assert.IsNotNull(err, opt);
            StringAssert.Contains(err, "stykker web");
        }
    }

    [TestMethod]
    public void DevOptions_OnlyInDebugBuilds()
    {
        var (a, err) = CliArgs.Parse(new[] { "--sim", "--port", "8099", "--data-dir", "x" });
#if DEBUG
        Assert.IsNull(err);
        Assert.IsTrue(a.Sim);
        Assert.AreEqual(8099, a.Port);
        Assert.AreEqual("x", a.DataDir);
#else
        Assert.IsNotNull(err, "Release kennt keine Entwickler-Schalter");
        Assert.IsFalse(a.Sim);
#endif
    }
}

[TestClass]
public class T8_AnsiTests
{
    private const string Red = "\u001b[31m", Reset = "\u001b[0m";

    [TestMethod]
    public void Width_IgnoresColorCodes()
    {
        Assert.AreEqual(5, Ansi.Width(Red + "hello" + Reset));
        Assert.AreEqual("hello", Ansi.Strip(Red + "hel" + Reset + "lo"));
    }

    [TestMethod]
    public void Fit_CutsVisibleCharsKeepsCodes_AndPads()
    {
        var s = Ansi.Fit(Red + "abcdefgh" + Reset, 5);
        Assert.AreEqual(5, Ansi.Width(s));
        Assert.AreEqual("abcd…", Ansi.Strip(s));
        Assert.IsTrue(s.EndsWith(Reset));
        Assert.AreEqual(8, Ansi.Width(Ansi.Fit("ab", 8, pad: true)));
    }

    [TestMethod]
    public void Palette_FallsBackTo256And16()
    {
        var c = new Rgb(255, 0, 0);
        Assert.AreEqual(196, Paint.To256(c));
        Assert.AreEqual(31, Paint.To16(c));
        Assert.AreEqual("", new Paint(TuiTheme.DeepSea, ColorMode.None).Fg(c));
    }
}

[TestClass]
public class T8_DisplayTests
{
    private static async Task<(VirtualTerminal Term, TuiApp App, List<string> Opened)> Run(string keys, int w = 110, int h = 34)
    {
        using var source = new SimSource();
        var term = new VirtualTerminal(w, h, keys);
        var opened = new List<string>();
        var app = new TuiApp(term, source, opened.Add, intervalMs: 250);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await app.RunAsync(cts.Token);
        return (term, app, opened);
    }

    private static string Text(VirtualTerminal t) => string.Join("\n", t.Screen.Select(Ansi.Strip));

    [TestMethod]
    public async Task Start_ShowsGpuServersWebAddressAndKeys_NoInputBox()
    {
        var (t, app, _) = await Run("{wait 1500}");
        var text = Text(t);
        Assert.AreEqual(34, t.Screen.Length);
        StringAssert.Contains(text, "STYKKER LLM");
        StringAssert.Contains(text, "SIMULATION");
        StringAssert.Contains(text, "GPU");
        StringAssert.Contains(text, "qwen3-8b");
        StringAssert.Contains(text, Strings.TuiWebLabel + "  http://127.0.0.1:8078");
        StringAssert.Contains(Ansi.Strip(t.Screen[^1]), "w web");
        Assert.IsFalse(text.Contains("│ >"), "kein Eingabefeld mehr");
        Assert.AreEqual(TuiApp.Panel.None, app.Current);
        Assert.IsTrue(t.Screen.All(l => Ansi.Width(l) <= 110), "keine Zeile ist breiter als das Terminal");
    }

    [TestMethod]
    public async Task W_OpensTheWebSignedIn_WithoutLeaving()
    {
        var (t, _, opened) = await Run("{wait 800}w{wait 300}");
        Assert.AreEqual(1, opened.Count);
        StringAssert.StartsWith(opened[0], "http://127.0.0.1:8078");
        StringAssert.Contains(Text(t), Strings.TuiWebOpened);
    }

    [TestMethod]
    public async Task Panels_OpenWithTheirKey_AndCloseWithEscape()
    {
        var (help, app1, _) = await Run("{wait 800}?{wait 300}");
        Assert.AreEqual(TuiApp.Panel.Help, app1.Current);
        StringAssert.Contains(Text(help), Strings.TuiWebOnly);

        var (recent, app2, _) = await Run("{wait 4000}r{wait 300}");
        Assert.AreEqual(TuiApp.Panel.Recent, app2.Current);
        StringAssert.Contains(Text(recent), Strings.RecentTitle);

        var (mem, app3, _) = await Run("{wait 800}m{wait 300}");
        Assert.AreEqual(TuiApp.Panel.Memory, app3.Current);
        StringAssert.Contains(Text(mem), "VRAM");

        var (_, app4, _) = await Run("{wait 800}m{wait 200}{esc}{wait 200}");
        Assert.AreEqual(TuiApp.Panel.None, app4.Current);
    }

    [TestMethod]
    public async Task ArrowsAndEnter_ShowTheDetailsOfTheChosenServer()
    {
        var (t, app, _) = await Run("{wait 1500}{down}{enter}{wait 400}");
        Assert.AreEqual(TuiApp.Panel.Details, app.Current);
        var text = Text(t);
        StringAssert.Contains(text, " ›");
        StringAssert.Contains(text, Strings.TuiModel);
        StringAssert.Contains(text, "slot 0");
    }

    [TestMethod]
    public async Task Code_WithoutAccess_SaysSo()
    {
        var (t, app, _) = await Run("{wait 800}c{wait 300}");
        Assert.AreEqual(TuiApp.Panel.Code, app.Current);
        StringAssert.Contains(Text(t), Strings.TuiNoCode);
    }

    [TestMethod]
    public void TailscaleAddresses_AreRecognized()
    {
        Assert.IsTrue(TuiApp.IsTailscale("100.101.102.103"));
        Assert.IsTrue(TuiApp.IsTailscale("100.64.0.1"));
        Assert.IsFalse(TuiApp.IsTailscale("100.128.0.1"));
        Assert.IsFalse(TuiApp.IsTailscale("192.168.178.20"));
    }

    [TestMethod]
    public async Task StatusLines_AreTheHeaderOnce_WithoutColors()
    {
        using var source = new SimSource();
        StateSnapshot? st = null;
        for (int i = 0; i < 3; i++) st = await source.PollAsync(CancellationToken.None);
        var lines = TuiApp.StatusLines(st!, 8078, 100, color: false).ToList();
        Assert.IsTrue(lines.Any(l => l.Contains("qwen3-8b")));
        Assert.IsTrue(lines.Any(l => l.Contains("http://127.0.0.1:8078")));
        Assert.IsFalse(lines.Any(l => l.Contains('\u001b')), "ohne Farben");
    }
}
