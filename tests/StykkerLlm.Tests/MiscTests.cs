using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class LogPatternTests
{
    [TestMethod]
    public void LiveLine()
    {
        var m = LogPatterns.Live().Match("1.0 I slot print_timing: id  0 | task 7 | n_gen =    149, tg =  49.31 t/s, tg_3s =  49.64 t/s");
        Assert.IsTrue(m.Success);
        Assert.AreEqual(0, LogPatterns.I(m, 1));
        Assert.AreEqual(7, LogPatterns.I(m, 2));
        Assert.AreEqual(149, LogPatterns.I(m, 3));
        Assert.AreEqual(49.64, LogPatterns.D(m, 5), 1e-9);
    }

    [TestMethod]
    public void TimingLines()
    {
        var p = LogPatterns.Prompt().Match("I slot print_timing: id  0 | task 2117 | prompt eval time =   11536.75 ms / 11736 tokens (    0.98 ms per token,  1017.27 tokens per second)");
        Assert.IsTrue(p.Success);
        Assert.AreEqual(2117, LogPatterns.I(p, 1));
        Assert.AreEqual(11736, LogPatterns.I(p, 3));
        Assert.AreEqual(1017.27, LogPatterns.D(p, 4), 1e-9);
        var e = LogPatterns.Eval().Match("I slot print_timing: id  0 | task 2117 |        eval time =    1393.75 ms /    70 tokens (   20.20 ms per token,    49.51 tokens per second)");
        Assert.IsTrue(e.Success);
        Assert.AreEqual(70, LogPatterns.I(e, 3));
        Assert.IsFalse(LogPatterns.Eval().IsMatch("task 1 |  prompt eval time =  1 ms / 2 tokens (0 ms per token, 1.0 tokens per second)"));
        var t = LogPatterns.Total().Match("I slot print_timing: id  0 | task 2117 |       total time =   12930.50 ms / 11806 tokens");
        Assert.AreEqual(12930.5, LogPatterns.D(t, 2), 1e-9);
    }

    [TestMethod]
    public void ReleaseProgressCancelUnified()
    {
        var r = LogPatterns.Release().Match("I slot      release: id  0 | task 2117 | stop processing: n_tokens = 27396, truncated = 1");
        Assert.AreEqual("1", r.Groups[4].Value);
        var g = LogPatterns.Progress().Match("I slot print_timing: id  0 | task 0 | prompt processing, n_tokens =   6144, progress = 0.25, t =   4.26 s / 1442.23 tokens per second");
        Assert.AreEqual(0.25, LogPatterns.D(g, 4), 1e-9);
        Assert.AreEqual(5, LogPatterns.I(LogPatterns.Cancel().Match("srv cancel task, id_task = 5"), 1));
        Assert.AreEqual("false", LogPatterns.Unified().Match("srv load_model: initializing, n_slots = 1, n_ctx_slot = 65536, kv_unified = 'false'").Groups[1].Value);
    }
}

[TestClass]
public class SettingsTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "slm-set-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [TestMethod]
    public void ImportsLegacyMonitorJsonOnce()
    {
        var dir = TempDir();
        try
        {
            var legacy = Path.Combine(dir, "monitor.json");
            File.WriteAllText(legacy, "{\"Servers\":[{\"Name\":\"Bonsai\",\"Url\":\"http://127.0.0.1:8081\"},{\"Name\":\"Box\",\"Url\":\"http://192.168.1.5:8080\",\"Log\":\"x.log\"}]," +
                "\"IntervalMs\":2000,\"CsvLog\":\"D:\\\\r.csv\",\"Theme\":\"Tiefsee\",\"OverlayVisible\":true,\"OverlayX\":10,\"OverlayY\":20}");
            var path = Path.Combine(dir, "data", "settings.json");
            var s = AppSettings.Load(path, legacy);
            Assert.AreEqual("Dark", s.Theme);   // Tiefsee gibt es nicht mehr: frühere Themen werden zu Dark
            Assert.AreEqual(2000, s.IntervalMs);
            Assert.AreEqual("D:\\r.csv", s.CsvLog);
            Assert.IsTrue(s.OverlayVisible);
            Assert.AreEqual(10, s.OverlayX);
            Assert.AreEqual(1, s.ManualServers.Count);   // nur der Server auf einem anderen Rechner
            Assert.AreEqual("Box", s.ManualServers[0].Name);
            Assert.IsTrue(File.Exists(path));
            // zweiter Start: settings.json gewinnt, die alte Datei wird nicht erneut gelesen
            File.WriteAllText(legacy, "{\"Theme\":\"Spacepunk Titan\"}");
            Assert.AreEqual("Dark", AppSettings.Load(path, legacy).Theme);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void BrokenSettingsAreNeverOverwritten()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ nope");
            var s = AppSettings.Load(path);
            Assert.IsTrue(s.Broken);
            s.Theme = "Obsidian";
            s.Save();
            Assert.AreEqual("{ nope", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void LegacyThemeNames()
    {
        // die früheren Themen gibt es nicht mehr: alle werden zu Dark
        Assert.AreEqual("Dark", AppSettings.LegacyThemeName("Weltraum-Glas"));
        Assert.AreEqual("Dark", AppSettings.LegacyThemeName("Cyber-Grid"));
        Assert.AreEqual("Dark", AppSettings.LegacyThemeName("Obsidian"));
        Assert.AreEqual("Spacepunk Titan", AppSettings.LegacyThemeName("titan"));
    }
}

[TestClass]
public class RequestLogTests
{
    private static FinishedRequest Req(string server = "s,1") =>
        new(1, server, "m", 1, 0, 100, 50.5, 40.25, 200, 5.5, new DateTime(2026, 1, 1, 1, 2, 3), ReqStatus.Truncated, "Aider");

    [TestMethod]
    public void WritesEnglishInvariantCsvAndSkipsLogEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-csv-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "r.csv");
        try
        {
            var log = new RequestLog(path);
            log.Append(Req());
            log.Append(Req() with { Seen = null });
            var lines = File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
            Assert.IsTrue(lines[0].TrimStart('\uFEFF').StartsWith("Time,Server"));
            Assert.AreEqual("2026-01-01 01:02:03,\"s,1\",m,Aider,100,50.5,200,40.3,5.5,truncated", lines[1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ForeignHeaderGoesToNeighbourFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "r.csv");
        try
        {
            File.WriteAllText(path, "Zeit;Server;Modell\r\n");
            new RequestLog(path).Append(Req("s"));
            Assert.AreEqual("Zeit;Server;Modell\r\n", File.ReadAllText(path));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "r.v2.csv")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

[TestClass]
public class ClientNamerTests
{
    [TestMethod]
    public void NamesGroupAndPrettify()
    {
        var p = new FakePlatform();
        p.Processes[20] = new ProcessDetails(20, 1, 1, "C:\\Apps\\qwen-code-desktop\\app.exe", null, null, new Dictionary<string, string>());
        p.Processes[21] = new ProcessDetails(21, 1, 1, "C:\\node\\node.exe", "node C:\\tools\\aider\\x.js", null, new Dictionary<string, string>());
        p.Processes[22] = new ProcessDetails(22, 1, 1, "C:\\x\\curl.exe", null, null, new Dictionary<string, string>());
        p.Processes[23] = new ProcessDetails(23, 1, 1, "C:\\x\\curl.exe", null, null, new Dictionary<string, string>());
        var conns = new[]
        {
            new ConnectionInfo("127.0.0.1", 50000, "127.0.0.1", 8081, 20), new ConnectionInfo("::1", 50001, "::1", 8081, 21),
            new ConnectionInfo("127.0.0.1", 50002, "127.0.0.1", 8081, 22), new ConnectionInfo("127.0.0.1", 50003, "127.0.0.1", 8081, 23),
            new ConnectionInfo("127.0.0.1", 50004, "127.0.0.1", 8081, 99),                       // wird ausgeschlossen
            new ConnectionInfo("127.0.0.1", 50005, "127.0.0.1", 9999, 24),                       // anderer Port
            new ConnectionInfo("10.0.0.2", 50006, "10.0.0.9", 8081, 25),                         // Gegenstelle nicht lokal
        };
        var names = new ClientNamer(p).Names(conns, 8081, 99);
        CollectionAssert.AreEqual(new[] { "Aider", "Qwen Desktop", "curl ×2" }, names);
    }
}
