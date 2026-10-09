using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Antworten per Skript, zählt Rückfragen
internal sealed class FakePrompt : IUserPrompt
{
    public List<string> Confirms { get; } = new();
    public List<string> Infos { get; } = new();
    public List<string> Secrets { get; } = new();
    public bool ConfirmAnswer { get; set; } = true;
    public string? SecretAnswer { get; set; } = "s3cret";

    public Task<string?> AskSecretAsync(string title, string text) { Secrets.Add(title); return Task.FromResult(SecretAnswer); }
    public Task<bool> ConfirmAsync(string title, string text, bool warning = false) { Confirms.Add(title + "|" + text); return Task.FromResult(ConfirmAnswer); }
    public Task InformAsync(string title, string text, bool warning = false) { Infos.Add(title + "|" + text); return Task.CompletedTask; }
}

[TestClass]
public class SingleInstanceTests
{
    [TestMethod]
    public void SecondInstanceIsNotFirst_AndSignalsTheFirst()
    {
        var name = SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "slm-si-" + Guid.NewGuid().ToString("N")));
        using var first = new SingleInstance(name);
        Assert.IsTrue(first.IsFirst);
        using var got = new ManualResetEventSlim();
        first.ActivationRequested += () => got.Set();
        using (var second = new SingleInstance(name))
        {
            Assert.IsFalse(second.IsFirst);
            second.SignalFirst();
        }
        Assert.IsTrue(got.Wait(3000), "the first instance was not asked to show its window");
    }

    [TestMethod]
    public void DifferentDataFoldersDoNotBlockEachOther()
    {
        using var a = new SingleInstance(SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "slm-si-a-" + Guid.NewGuid().ToString("N"))));
        using var b = new SingleInstance(SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "slm-si-b-" + Guid.NewGuid().ToString("N"))));
        Assert.IsTrue(a.IsFirst && b.IsFirst);
    }

    [TestMethod]
    public void InstanceCanBeStartedAgainAfterTheFirstEnds()
    {
        var name = SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "slm-si-" + Guid.NewGuid().ToString("N")));
        using (var first = new SingleInstance(name)) Assert.IsTrue(first.IsFirst);
        using var again = new SingleInstance(name);
        Assert.IsTrue(again.IsFirst);
    }
}

[TestClass]
public class DetectionPrivacyTests
{
    private const string Llama = "C:\\llama\\llama-server.exe";

    [TestMethod]
    public async Task UnrelatedListener_IsNeverReadDeeply_NoProcessMemoryTouched()
    {
        var p = new FakePlatform();
        var h = new FakeHandler();
        p.AddServer(41, "C:\\apps\\web.exe", "web.exe", 5000);
        p.AddServer(42, "C:\\apps\\db.exe", "db.exe --secret x", 5001);
        var d = new ServerDiscovery(p, null, new HttpClient(h), selfPid: 1);
        await d.RunAsync(); await d.RunAsync();
        Assert.AreEqual(0, p.ReadProcessCalls, "PEB read for processes that are not model servers");
        Assert.AreEqual(2, p.ReadBasicCalls);   // je Prozess einmal (gemerkt), nur mit eingeschränktem Handle
    }

    [TestMethod]
    public async Task PositiveProbe_ThenReadsCommandLineOnce()
    {
        var p = new FakePlatform();
        var h = new FakeHandler();
        p.AddServer(40, "C:\\tools\\myserver.exe", "myserver.exe -m x.gguf --port 7000", 7000);
        h.Routes["127.0.0.1:7000/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        var d = new ServerDiscovery(p, null, new HttpClient(h), selfPid: 1);
        var s = (await d.RunAsync()).Servers.Single();
        Assert.AreEqual(1, p.ReadProcessCalls);
        Assert.IsTrue(s.CommandLineReadable);
        await d.RunAsync(); await d.RunAsync();
        Assert.AreEqual(1, p.ReadProcessCalls);
    }

    [TestMethod]
    public async Task NamedLlamaServer_IsReadDeeplyImmediately()
    {
        var p = new FakePlatform();
        p.AddServer(30, Llama, Llama + " -m a.gguf --port 8081", 8081);
        var d = new ServerDiscovery(p, null, new HttpClient(new FakeHandler()), selfPid: 1);
        await d.RunAsync();
        Assert.AreEqual(1, p.ReadProcessCalls);
    }

    [TestMethod]
    public void ApiKeyFile_IsResolvedFromTheServersWorkingFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "key.txt"), "\nfromcwd\n");
            var d = new ProcessDetails(1, 1, 0, Llama, Llama + " -m x --api-key-file key.txt", dir, new Dictionary<string, string>());
            Assert.AreEqual("fromcwd", ServerDiscovery.ExtractApiKey(d)!.Reveal());
            var d2 = d with { CommandLine = Llama + " --api-key-file=key.txt" };
            Assert.AreEqual("fromcwd", ServerDiscovery.ExtractApiKey(d2)!.Reveal());
            Assert.IsNull(ServerDiscovery.ExtractApiKey(d with { WorkingDir = Path.GetTempPath() }));   // nicht relativ zum Monitor
        }
        finally { Directory.Delete(dir, true); }
    }
}

[TestClass]
public class FileSafetyTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "slm-f-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [TestMethod]
    public void AtomicFile_ParallelWritersLeaveValidFileAndNoTempFiles()
    {
        var dir = TempDir();
        try
        {
            var f = Path.Combine(dir, "x.json");
            Parallel.For(0, 40, i =>
            {
                for (int k = 0; k < 5; k++)
                    try { AtomicFile.WriteAllText(f, new string((char)('a' + i % 26), 2000)); } catch (IOException) { /* Ersetzen kurz gesperrt: der nächste Schreiber gewinnt */ }
            });
            var text = File.ReadAllText(f);
            Assert.AreEqual(2000, text.Length);
            Assert.IsTrue(text.All(c => c == text[0]), "file content is mixed");
            // eigene Temp-Dateien bleiben nie liegen; "~RF*.TMP" legt Windows selbst an, wenn ReplaceFile unter Wettlauf scheitert (CleanTemp räumt auch die ab)
            Assert.AreEqual(0, Directory.GetFiles(dir).Count(f => !f.EndsWith("x.json") && !Path.GetFileName(f).Contains("~RF")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void AtomicFile_CleanTempRemovesOldLeftovers()
    {
        var dir = TempDir();
        try
        {
            var old = Path.Combine(dir, "a.json.1234abcd.tmp"); File.WriteAllText(old, "x"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
            var fresh = Path.Combine(dir, "b.json.1234abcd.tmp"); File.WriteAllText(fresh, "x");
            AtomicFile.CleanTemp(dir, TimeSpan.FromHours(1));
            Assert.IsFalse(File.Exists(old));
            Assert.IsTrue(File.Exists(fresh));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Library_KeepsOnlyTheLastFiveBadFiles()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "library.json");
            for (int i = 0; i < 8; i++)
            {
                File.WriteAllText(path, "{ not json " + i);
                var lib = Library.Load(path);
                Assert.IsNotNull(lib.RecoveredFrom);
                File.SetLastWriteTimeUtc(lib.RecoveredFrom!, DateTime.UtcNow.AddMinutes(-100 + i));
                Library.PruneBad(path);
            }
            Assert.AreEqual(Library.KeepBadFiles, Directory.GetFiles(dir, "library.json.bad-*").Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void RequestLog_RotatesWhenTooBig()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "requests.csv");
            var log = new RequestLog(csv) { MaxBytes = 300 };
            for (int i = 0; i < 12; i++)
                log.Append(new FinishedRequest(i, "s", "m", i, 0, i, 100, 50, 5, 1, DateTime.Now, ReqStatus.Done, "c", i, "k"));
            Assert.IsTrue(File.Exists(Path.ChangeExtension(csv, ".1.csv")), "old log was not moved aside");
            Assert.IsTrue(new FileInfo(csv).Length < 1200);
            Assert.IsTrue(File.ReadAllText(csv).StartsWith(RequestLog.Header.TrimStart('\uFEFF')) || File.ReadAllText(csv).Contains("PromptTokens"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string WriteRecording(string dir, string id, bool end, bool truncatedTail, DateTime written)
    {
        var f = Path.Combine(dir, id + ".jsonl");
        var lines = new List<string>
        {
            $"{{\"t\":\"meta\",\"v\":1,\"id\":\"{id}\",\"started\":\"{DateTime.Now.AddMinutes(-5):o}\",\"target\":\"k\",\"model\":\"m\",\"gpu\":\"g\",\"proxy\":false,\"keys\":[\"k\"],\"names\":[\"n\"]}}",
            "{\"t\":\"s\",\"ts\":1,\"sv\":[{\"k\":\"k\",\"tps\":10,\"cu\":0,\"cm\":0,\"sl\":[],\"cl\":[]}]}",
            "{\"t\":\"s\",\"ts\":2,\"sv\":[{\"k\":\"k\",\"tps\":12,\"cu\":0,\"cm\":0,\"sl\":[],\"cl\":[]}]}",
        };
        if (end) lines.Add($"{{\"t\":\"end\",\"ended\":\"{DateTime.Now:o}\"}}");
        var text = string.Join("\n", lines) + "\n" + (truncatedTail ? "{\"t\":\"s\",\"ts\":3,\"sv\":[{\"k\":\"k\",\"tp" : "");
        File.WriteAllText(f, text);
        File.SetLastWriteTime(f, written);
        return f;
    }

    [TestMethod]
    public void CrashedRecording_IsRepairedOnce_AndSummarised()
    {
        var dir = TempDir();
        try
        {
            var crashed = WriteRecording(dir, "20260101-000000_a", end: false, truncatedTail: true, written: DateTime.Now.AddHours(-2));
            var fine = WriteRecording(dir, "20260101-000100_b", end: true, truncatedTail: false, written: DateTime.Now.AddHours(-2));
            var recent = WriteRecording(dir, "20260101-000200_c", end: false, truncatedTail: false, written: DateTime.Now);   // zu frisch: könnte laufen
            Assert.AreEqual(1, RecordingStore.RepairCrashed(dir));
            Assert.AreEqual(0, RecordingStore.RepairCrashed(dir));   // einmalig
            var d = RecordingStore.Load(crashed)!;
            Assert.IsNotNull(d.Ended);
            Assert.AreEqual(2, d.Samples.Count);   // die abgebrochene Zeile ist weg, der Rest bleibt
            Assert.IsTrue(File.Exists(RecordingStore.SummaryPath(crashed)));
            Assert.IsNull(RecordingStore.Load(recent)!.Ended);
            Assert.IsFalse(File.Exists(RecordingStore.SummaryPath(recent)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void RecordingPrune_RemovesOldestByCountAndSize_KeepsActive()
    {
        var dir = TempDir();
        try
        {
            var files = Enumerable.Range(0, 6).Select(i => WriteRecording(dir, $"2026010{i}-000000_x", true, false, DateTime.Now.AddMinutes(-60 + i))).ToList();
            Assert.AreEqual(3, RecordingStore.Prune(dir, maxCount: 3, maxBytes: long.MaxValue, keep: new[] { files[0] }));
            var left = Directory.GetFiles(dir, "*.jsonl").Select(Path.GetFileName).OrderBy(x => x).ToArray();
            Assert.AreEqual(3, left.Length);
            Assert.IsTrue(File.Exists(files[0]), "active recording was pruned");   // die aktive bleibt, obwohl sie die älteste ist
            Assert.IsTrue(File.Exists(files[5]));
            RecordingStore.Prune(dir, int.MaxValue, 1);   // Größenlimit: alles außer dem Neuesten weg ... bis nichts mehr geht
            Assert.AreEqual(0, Directory.GetFiles(dir, "*.jsonl").Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void LogPrune_HonoursCountLimit()
    {
        var dir = TempDir();
        try
        {
            for (int i = 0; i < 6; i++)
            {
                var f = Path.Combine(dir, $"l{i}.log"); File.WriteAllText(f, "x"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-10 + i));
            }
            ServerLauncher.PruneLogs(dir, long.MaxValue, new[] { Path.Combine(dir, "l0.log") }, maxFiles: 3);
            CollectionAssert.AreEqual(new[] { "l0.log", "l4.log", "l5.log" }, Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }
}

