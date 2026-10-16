using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N90: RAM-Bedarf je Server merken (höchster Working Set mit Kindprozessen), anzeigen und vor dem Start prüfen
[TestClass]
public class N90_RamMemoryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static ServerInfo Server(int pid = 10, long start = 1, string port = "8081")
    {
        var args = new List<string> { "-m", "m.gguf", "--port", port, "-c", "4096" };
        return new ServerInfo
        {
            Key = "127.0.0.1:" + port, Port = int.Parse(port), Pid = pid, StartTicks = start, Program = "C:\\x\\llama-server.exe",
            Args = args, CommandLineReadable = true, Params = LlamaServerArgs.Parse(args), WorkingDir = "C:\\w",
        };
    }

    private static SystemSample Sys(double total, double used) => new(10, 8, used, total, used, total);

    // Verlauf: der höchste gemessene RAM bleibt, auch wenn er später fällt; Beobachtungen ohne RAM ändern nichts
    [TestMethod]
    public void Library_KeepsTheHighestRamSeen()
    {
        var lib = new Library();
        var s = Server();
        lib.Observe(s, new Observation(40, null, null, RamGb: 4.0), T0);
        lib.Observe(s, new Observation(40, null, null, RamGb: 17.5), T0.AddSeconds(1));
        lib.Observe(s, new Observation(40, null, null, RamGb: 9.0), T0.AddSeconds(2));
        lib.Observe(s, new Observation(40, null, null), T0.AddSeconds(3));
        Assert.AreEqual(17.5, lib.History.Single().MaxRamGb, 1e-9);
    }

    // Prozessbaum: Starter → Python → Engine zählen zusammen; ein eigener Kindserver (skip) und sein Baum nicht
    [TestMethod]
    public void ProcessTree_SumsTheWholeTreeExceptSkippedChildren()
    {
        var p = new FakePlatform();
        p.Usage[100] = new ProcessUsage(1.0, 1.0, TimeSpan.Zero);     // Starter (venv)
        p.Usage[101] = new ProcessUsage(2.0, 2.0, TimeSpan.Zero);     // Python
        p.Usage[102] = new ProcessUsage(14.0, 14.0, TimeSpan.Zero);   // Engine
        p.Usage[200] = new ProcessUsage(5.0, 5.0, TimeSpan.Zero);     // eigener Kindserver (wird separat beobachtet)
        p.Usage[201] = new ProcessUsage(0.5, 0.5, TimeSpan.Zero);     // dessen Engine
        p.Children[100] = new() { (101, "python") };
        p.Children[101] = new() { (102, "strata"), (200, "llama-server") };
        p.Children[200] = new() { (201, "llama-server") };

        Assert.AreEqual(17.0, ProcessTree.WorkingSetGb(p, 100, new HashSet<int> { 200 })!.Value, 1e-9);
        Assert.AreEqual(22.5, ProcessTree.WorkingSetGb(p, 100)!.Value, 1e-9, "ohne Ausnahme zählt alles");
        Assert.IsNull(ProcessTree.WorkingSetGb(p, 999), "nichts lesbar = null, nicht 0");
    }

    // Die Vorprüfung warnt, wenn der gemerkte RAM mehr ist als der freie RAM (+ 1 GB); nur Warnung, kein Fehler
    [TestMethod]
    public void Check_WarnsWhenMeasuredRamIsNotFree_OnlyAsWarning()
    {
        var p = new FakePlatform { SystemValues = Sys(32, 22.9) };   // 9.1 GB frei
        var big = ServerLauncher.Check(new LaunchSpec("n", "llama-server.exe", new[] { "-m", "m.gguf" }, null, null, null, null, 17.5), p, null, Array.Empty<ServerInfo>());
        var w = big.Single(i => i.Code == "ram-measured");
        Assert.AreEqual(LaunchSeverity.Warning, w.Severity);
        StringAssert.Contains(w.Message, "17.5 GB RAM");
        StringAssert.Contains(w.Message, "9.1 GB free");

        var small = ServerLauncher.Check(new LaunchSpec("n", "llama-server.exe", new[] { "-m", "m.gguf" }, null, null, null, null, 9.5), p, null, Array.Empty<ServerInfo>());
        Assert.IsFalse(small.Any(i => i.Code == "ram-measured"), "9.5 GB passt in 9.1 GB + 1 GB Puffer");

        var unknown = ServerLauncher.Check(new LaunchSpec("n", "llama-server.exe", new[] { "-m", "m.gguf" }, null, null, null, null, 99), new FakePlatform(), null, Array.Empty<ServerInfo>());
        Assert.IsFalse(unknown.Any(i => i.Code == "ram-measured"), "ohne Systemwerte keine Aussage");
    }

    // Zustand: maxRamGb steht im Verlauf der Bibliothek und kommt in der Zustands-Antwort an
    [TestMethod]
    public async Task State_CarriesMaxRamOfHistory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "n90-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(paths.LibraryFile, """
                {"Version":1,"Profiles":[],"History":[{"Key":"k1","Program":"C:\\bin\\strata.exe","Args":["server.py"],
                 "Name":"Strata","Runs":2,"TotalSeconds":60,"MaxVramGb":14.2,"MaxRamGb":17.5}]}
                """);
            using var e = new MonitorEngine(new FakePlatform(), paths, new AppSettings(), new HttpClient(new FakeHandler()));
            var state = StateSnapshot.Parse(StateJson.WriteText(e, null, null, 8078, DateTimeOffset.Now));
            Assert.AreEqual(17.5, state.History.Single().MaxRamGb, 1e-9);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // Verlaufsdetails und Anzeigetexte: RAM-Zeile, Balken „needs 10.3 · 14.6 free“, Warnung nur bei needs > free
    [TestMethod]
    public void Details_ShowRamMaxAndForecastText()
    {
        var h = new HistoryEntry { Key = "k", Program = "p.exe", Runs = 1, MaxVramGb = 14.2, MaxRamGb = 17.5 };
        var runs = HistoryDetails.Runs(h);
        Assert.AreEqual("17.5 GB", runs.First(f => f.Key == Strings.RowRamMax).Value);
        Assert.AreEqual("needs 10.3 · 14.6 free", Strings.PfNeedsFree(10.3, 14.6));
        StringAssert.Contains(Strings.PfRamForecastTip(17.5, 9.1), "highest RAM use seen (with child processes)");
        StringAssert.Contains(Strings.PfForecastTip(14.2, 1.0, true), "free = free right now");
        Assert.IsNull(HistoryDetails.Runs(new HistoryEntry { Key = "k", Program = "p.exe" }).FirstOrDefault(f => f.Key == Strings.RowRamMax).Value,
            "ohne Messung keine Zeile");
    }

    // Aufnahmen: der höchste RAM je Server wird in der Zusammenfassung mitgezählt
    [TestMethod]
    public void RecordingSummary_HasMaxRam()
    {
        var a = new RecordingSummary { Model = "A", AvgTps = 50, PeakTps = 60, MaxVramGb = 6, MaxRamGb = 17.5, EnergyWh = 1, TokensOut = 1000, Started = T0 };
        var b = new RecordingSummary { Model = "B", AvgTps = 100, PeakTps = 90, MaxVramGb = 5, MaxRamGb = 9, EnergyWh = 2, TokensOut = 1000, Started = T0 };
        Assert.AreEqual(17.5, a.MaxRamGb, 1e-9);
        Assert.IsTrue(RecordingMetrics.Compare(a, b).Any(r => r.Label == "Max RAM"));
    }
}
