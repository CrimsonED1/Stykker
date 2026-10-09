using System.Net;
using System.Net.Sockets;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N2 (Befunde aus T6): Startbestätigung mit Geheimschlüssel, Leerlauf-Zeitlimit im Proxy, Zeilenlänge bei Aufnahmen, CsvLog im Datenordner
[TestClass]
public class N2_ConfirmKeyTests
{
    internal static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "slm-n2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [TestMethod]
    public void Key_IsCreatedProtectedAndReused()
    {
        var dir = TempDir();
        try
        {
            var plat = new FakePlatform { UserProtection = true };
            var path = Path.Combine(dir, "confirm.key");
            var k1 = ConfirmKey.LoadOrCreate(path, plat);
            Assert.AreEqual(ConfirmKey.Size, k1.Length);
            Assert.IsTrue(File.Exists(path));
            // in der Datei steht nie der Klartext-Schlüssel
            Assert.IsFalse(File.ReadAllText(path).Contains(Convert.ToBase64String(k1)));
            CollectionAssert.AreEqual(k1, ConfirmKey.LoadOrCreate(path, plat));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void Key_InvalidFileGivesANewKey_AndWithoutProtectionNothingIsWritten()
    {
        var dir = TempDir();
        try
        {
            var plat = new FakePlatform { UserProtection = true };
            var path = Path.Combine(dir, "confirm.key");
            var k1 = ConfirmKey.LoadOrCreate(path, plat);
            File.WriteAllText(path, "kein base64 !!");
            var k2 = ConfirmKey.LoadOrCreate(path, plat);
            CollectionAssert.AreNotEqual(k1, k2);
            CollectionAssert.AreEqual(k2, ConfirmKey.LoadOrCreate(path, plat));   // die Datei wurde neu geschrieben

            // fremder Benutzer / Rechner: Entschlüsselung scheitert -> neuer Schlüssel
            File.WriteAllText(path, Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));
            CollectionAssert.AreNotEqual(k2, ConfirmKey.LoadOrCreate(path, plat));

            var other = Path.Combine(dir, "x", "confirm.key");
            var k3 = ConfirmKey.LoadOrCreate(other, new FakePlatform());   // keine Benutzerbindung
            Assert.AreEqual(ConfirmKey.Size, k3.Length);
            Assert.IsFalse(File.Exists(other));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void Fingerprint_DependsOnTheKey_AndOldPlainHashesDoNotMatch()
    {
        var spec = new LaunchSpec("n", "C:\\llama\\llama-server.exe", new[] { "-m", "x.gguf" }, null);
        var a = new Library { ConfirmKey = Enumerable.Repeat((byte)1, 32).ToArray() };
        var b = new Library { ConfirmKey = Enumerable.Repeat((byte)2, 32).ToArray() };
        StringAssert.StartsWith(a.Fingerprint(spec), "v2:");
        Assert.AreEqual(a.Fingerprint(spec), a.Fingerprint(spec));
        Assert.AreNotEqual(a.Fingerprint(spec), b.Fingerprint(spec));
        Assert.AreNotEqual(a.Fingerprint(spec), a.Fingerprint(spec with { Args = new[] { "-m", "y.gguf" } }));
        Assert.AreNotEqual(a.Fingerprint(spec), ServerLauncher.Fingerprint(spec));   // der alte, ungeschützte Hash ist kein gültiger Wert mehr
    }

    private sealed class Rig : IDisposable
    {
        public string Dir = TempDir();
        public FakePlatform Platform = new() { UserProtection = true };
        public FakePrompt Prompt = new();
        public MonitorEngine Engine = null!;
        public LaunchCoordinator Coordinator = null!;
        public string Exe = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        public Rig()
        {
            File.WriteAllText(Path.Combine(Dir, "m.gguf"), "x");
            Open();
        }
        public void Open()
        {
            Engine = new MonitorEngine(Platform, new AppPaths(Dir), new AppSettings(), new HttpClient(new FakeHandler()));
            Coordinator = new LaunchCoordinator(Engine, Prompt);
        }
        public LaunchSpec Spec(int port) => new("P", Exe, new[] { "-m", Path.Combine(Dir, "m.gguf"), "--port", port.ToString(), "/c", "ping", "-n", "2", "127.0.0.1" }, null);
        public Profile Save(LaunchSpec spec)
        {
            var info = new ServerInfo { Key = "k", Port = 1, Program = spec.Program, CommandLineReadable = true, Args = spec.Args, Params = LlamaServerArgs.Parse(spec.Args), Env = new Dictionary<string, string>() };
            return Engine.Library.AddProfile(info, "P", DateTime.Now)!;
        }
        public void KillLaunches() { foreach (var l in Engine.Registry.Launches.ToList()) { try { l.Proc?.Kill(); } catch { } } }
        public void Dispose() { KillLaunches(); Engine.Dispose(); try { Directory.Delete(Dir, true); } catch { } }
    }

    [TestMethod]
    public async Task ConfirmationSurvivesRestart_ButNotATamperedLibraryOrALostKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rig = new Rig();
        var spec = rig.Spec(18231);
        var profile = rig.Save(spec);
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(profile));
        Assert.AreEqual(1, rig.Prompt.Confirms.Count);
        rig.KillLaunches();
        rig.Engine.Library.Save();
        rig.Engine.Dispose();

        // Neustart mit derselben confirm.key: keine Rückfrage
        rig.Open();
        rig.Prompt.Confirms.Clear();
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(rig.Engine.Library.GetProfile(profile.Id)!));
        Assert.AreEqual(0, rig.Prompt.Confirms.Count);
        rig.KillLaunches();
        rig.Engine.Library.Save();
        rig.Engine.Dispose();

        // Manipulation: jemand schreibt den alten, ohne Schlüssel berechenbaren Hash in library.json -> Dialog
        var libPath = new AppPaths(rig.Dir).LibraryFile;
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(libPath))!;
        var entries = doc["Profiles"]!.AsArray();
        Assert.IsTrue(entries.Count > 0);
        foreach (var h in entries) h!["ConfirmedFingerprint"] = ServerLauncher.Fingerprint(spec);
        File.WriteAllText(libPath, doc.ToJsonString());
        rig.Open();
        rig.Prompt.Confirms.Clear();
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(rig.Engine.Library.GetProfile(profile.Id)!));
        Assert.AreEqual(1, rig.Prompt.Confirms.Count, "ein selbst berechneter Fingerabdruck darf die Rückfrage nicht ersetzen");
        rig.KillLaunches();
        rig.Engine.Library.Save();
        rig.Engine.Dispose();

        // confirm.key gelöscht: alle Bestätigungen ungültig, erneut fragen
        File.Delete(new AppPaths(rig.Dir).ConfirmKeyFile);
        rig.Open();
        rig.Prompt.Confirms.Clear();
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(rig.Engine.Library.GetProfile(profile.Id)!));
        Assert.AreEqual(1, rig.Prompt.Confirms.Count);
        Assert.IsTrue(File.Exists(new AppPaths(rig.Dir).ConfirmKeyFile));
    }
}

[TestClass]
public class N2_ProxyIdleTests
{
    private static async Task<string> ReadAll(NetworkStream s, int ms)
    {
        var sb = new StringBuilder();
        var buf = new byte[1024];
        using var cts = new CancellationTokenSource(ms);
        try { while (true) { int n = await s.ReadAsync(buf, cts.Token); if (n <= 0) break; sb.Append(Encoding.Latin1.GetString(buf, 0, n)); } } catch { }
        return sb.ToString();
    }

    [TestMethod]
    public async Task StalledRequestBody_IsCutOffByTheIdleTimeout_LongBeforeTheTotalTimeout()
    {
        using var up = new RawUpstream();
        using var proxy = new RequestProxy("k", up.Url, 0) { RequestIdleTimeout = TimeSpan.FromMilliseconds(400) };
        proxy.Start();
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, proxy.ListenPort);
        var s = c.GetStream();
        // Kopf vollständig, Text nur zur Hälfte, dann Stille
        await s.WriteAsync(Encoding.ASCII.GetBytes("POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\n{\"a\":"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await ReadAll(s, 8000);
        StringAssert.StartsWith(res, "HTTP/1.1 408");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), "nach " + sw.Elapsed);
    }

    [TestMethod]
    public async Task SteadyTrickle_KeepsTheRequestAlive_UntilTheTotalLimit()
    {
        using var up = new RawUpstream();
        up.Handler = async (_, s, ct) => await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", ct);
        using var proxy = new RequestProxy("k", up.Url, 0) { RequestIdleTimeout = TimeSpan.FromMilliseconds(500) };
        proxy.Start();
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, proxy.ListenPort);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("POST /x HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 6\r\n\r\n"));
        // ein Byte alle 200 ms (kürzer als das Leerlauf-Zeitlimit): die Anfrage bleibt gültig
        foreach (var ch in "abcdef") { await s.WriteAsync(new[] { (byte)ch }); await Task.Delay(200); }
        StringAssert.StartsWith(await ReadAll(s, 5000), "HTTP/1.1 200");
    }
}

[TestClass]
public class N2_RecordingLineTests
{
    [TestMethod]
    public void ReadLineLimited_SkipsOverlongLines_AndKeepsTheRest()
    {
        var text = "short\r\n" + new string('x', 5000) + "\nafter\nlast";
        using var r = new StringReader(text);
        Assert.AreEqual("short", RecordingStore.ReadLineLimited(r, 100));
        Assert.AreEqual("", RecordingStore.ReadLineLimited(r, 100));   // zu lang: verworfen
        Assert.AreEqual("after", RecordingStore.ReadLineLimited(r, 100));
        Assert.AreEqual("last", RecordingStore.ReadLineLimited(r, 100));
        Assert.IsNull(RecordingStore.ReadLineLimited(r, 100));
    }

    [TestMethod]
    public void Load_SkipsAGiantLine_ButReadsTheLinesAroundIt()
    {
        var dir = N2_ConfirmKeyTests.TempDir();
        try
        {
            var path = Path.Combine(dir, "r.jsonl");
            var started = DateTime.Now.AddMinutes(-5).ToString("o");
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.Write($"{{\"t\":\"meta\",\"v\":1,\"id\":\"giant\",\"started\":\"{started}\",\"target\":\"k\",\"model\":\"m\",\"gpu\":\"g\",\"proxy\":false,\"keys\":[\"k\"],\"names\":[\"n\"]}}\n");
                w.Write("{\"t\":\"junk\",\"x\":\"");
                w.Write(new string('a', RecordingStore.MaxLineChars + 1000));   // mehr als die Grenze
                w.Write("\"}\n");
                w.Write($"{{\"t\":\"end\",\"ended\":\"{DateTime.Now:o}\"}}\n");
            }
            var data = RecordingStore.Load(path);
            Assert.IsNotNull(data);
            Assert.AreEqual("giant", data!.Meta.Id);
            Assert.IsNotNull(data.Ended);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

[TestClass]
public class N2_CsvLogTests
{
    [TestMethod]
    public void CsvLog_OnlyInsideTheDataFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "slm-n2-data-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        Assert.AreEqual(paths.DefaultCsv, paths.ResolveCsvLog(null, out var note)); Assert.IsNull(note);
        Assert.AreEqual(paths.DefaultCsv, paths.ResolveCsvLog("  ", out note)); Assert.IsNull(note);
        var inside = Path.Combine(root, "sub", "my.csv");
        Assert.AreEqual(inside, paths.ResolveCsvLog(inside, out note)); Assert.IsNull(note);

        // außerhalb, Verwandter mit gleichem Namenspräfix, ".." aus dem Ordner heraus, der Ordner selbst: Standardpfad + Hinweis
        foreach (var bad in new[] { Path.Combine(Path.GetTempPath(), "evil.csv"), root + "-other" + Path.DirectorySeparatorChar + "x.csv",
                     Path.Combine(root, "..", "evil.csv"), root + Path.DirectorySeparatorChar })
        {
            Assert.AreEqual(paths.DefaultCsv, paths.ResolveCsvLog(bad, out note), bad);
            Assert.IsNotNull(note, bad);
            StringAssert.Contains(note, paths.DefaultCsv);
        }
    }

    [TestMethod]
    public void Engine_UsesTheDefaultLogWhenSettingsPointOutside_AndSaysSo()
    {
        var root = Path.Combine(Path.GetTempPath(), "slm-n2-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outside = Path.Combine(Path.GetTempPath(), "slm-n2-evil-" + Guid.NewGuid().ToString("N") + ".csv");
            var settings = new AppSettings { CsvLog = outside };
            using var e = new MonitorEngine(new FakePlatform(), new AppPaths(root), settings, new HttpClient(new FakeHandler()));
            Assert.AreEqual(new AppPaths(root).DefaultCsv, e.Csv.Path);
            Assert.IsNotNull(e.CsvLogNote);
            Assert.IsNull(settings.CsvLog, "ungültiger Pfad wird bereinigt, damit der Hinweis nur einmal erscheint");
            Assert.IsFalse(File.Exists(outside));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

// N2 (C): Speicheranzeige wie im Task-Manager; Commit nur als Warnung ab 85 % / 95 %
[TestClass]
public class N2_MemoryDisplayTests
{
    private static SystemSample Sample(double ramUsed, double ramTotal, double commitUsed, double commitLimit) => new(10, 16, ramUsed, ramTotal, commitUsed, commitLimit);

    [TestMethod]
    public void Memory_IsUsedOverTotal_AsAFraction()
    {
        Assert.AreEqual(0.5, Sample(16, 32, 20, 80).RamFrac, 1e-9);
        Assert.AreEqual(0.0, Sample(0, 0, 0, 0).RamFrac);        // unbekannt: kein Teilen durch 0
        Assert.AreEqual(1.0, Sample(40, 32, 0, 0).RamFrac);      // nie über 100 %
    }

    [TestMethod]
    public void Commit_WarnsFrom85PercentAndIsCriticalFrom95Percent()
    {
        Assert.AreEqual(0, Sample(8, 32, 84.9, 100).CommitLevel);
        Assert.AreEqual(1, Sample(8, 32, 85, 100).CommitLevel);
        Assert.AreEqual(1, Sample(8, 32, 94.9, 100).CommitLevel);
        Assert.AreEqual(2, Sample(8, 32, 95, 100).CommitLevel);
        Assert.AreEqual(0, Sample(8, 32, 5, 0).CommitLevel);     // Grenze unbekannt: keine Warnung
    }

    [TestMethod]
    public void CommitWarningText_NamesTheNumbersAndTheTip()
    {
        var t = Strings.CommitLow(63.4, 70.0);
        StringAssert.Contains(t, "Memory reserve low: 63.4 of 70.0 GB committed");
        StringAssert.Contains(t, "programs may crash");
        StringAssert.Contains(t, "--cache-ram");
        StringAssert.Contains(t, "page file");
    }
}
