using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Absturz-Hinweis (ServerLost, CrashAnalysis) und Skript-Export (ScriptExport)
[TestClass]
public class F1_FeatureTests
{
    private static FakePlatform PlatformWithLlama(out int pid)
    {
        pid = 4711;
        var p = new FakePlatform();
        p.AddServer(pid, @"C:\llama\llama-server.exe", @"""C:\llama\llama-server.exe"" -m C:\models\m.gguf --port 8090", 8090, "127.0.0.1");
        return p;
    }

    private static async Task<ServerRegistry> DetectAsync(FakePlatform p)
    {
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        await reg.RefreshNowAsync();
        return reg;
    }

    [TestMethod]
    public async Task ServerThatVanishes_RaisesServerLost()
    {
        var p = PlatformWithLlama(out int pid);
        using var reg = await DetectAsync(p);
        Assert.AreEqual(1, reg.Servers.Count);
        var lost = new List<ServerLost>();
        reg.ServerLost += lost.Add;

        p.Processes.Remove(pid); p.Listeners.Clear();
        await reg.RefreshNowAsync();   // erster Durchlauf ohne Treffer: noch nicht entfernt
        await reg.RefreshNowAsync();   // zweiter: entfernt → Hinweis
        Assert.AreEqual(0, reg.Servers.Count);
        Assert.AreEqual(1, lost.Count);
        Assert.AreEqual(pid, lost[0].Pid);
    }

    [TestMethod]
    public async Task ServerStoppedByMonitor_DoesNotRaiseServerLost()
    {
        var p = PlatformWithLlama(out int pid);
        using var reg = await DetectAsync(p);
        var lost = new List<ServerLost>();
        reg.ServerLost += lost.Add;

        reg.ExpectStop(reg.Servers.Single().Key);
        p.Processes.Remove(pid); p.Listeners.Clear();
        await reg.RefreshNowAsync();
        await reg.RefreshNowAsync();
        Assert.AreEqual(0, reg.Servers.Count);
        Assert.AreEqual(0, lost.Count);
    }

    [TestMethod]
    public void CrashAnalysis_FindsLastKnownCause()
    {
        var lines = new[]
        {
            "load_model: loading model 'x.gguf'",
            "ggml_backend_cuda_buffer_type_alloc_buffer: allocating 9000 MiB on device 0: cudaMalloc failed: out of memory",
            "main: exiting due to model loading error",
        };
        StringAssert.Contains(CrashAnalysis.Guess(lines)!, "memory");
        Assert.IsNull(CrashAnalysis.Guess(new[] { "srv  llama_server: listening on http://127.0.0.1:8080" }));
        StringAssert.Contains(CrashAnalysis.Guess(new[] { "llama_model_load: error loading model: unknown model architecture: 'foo'" })!, "architecture");
    }

    [TestMethod]
    public void CrashAnalysis_ReadTail_ReturnsLastLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "stykker-tail-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(1, 100).Select(i => $"line {i}"));
            var tail = CrashAnalysis.ReadTail(path, 5);
            CollectionAssert.AreEqual(new[] { "line 96", "line 97", "line 98", "line 99", "line 100" }, tail.ToArray());
            Assert.AreEqual(0, CrashAnalysis.ReadTail(path + ".missing").Count);
            Assert.AreEqual(0, CrashAnalysis.ReadTail(null).Count);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ScriptExport_PowerShell_QuotesAndKeepsRedaction()
    {
        var ps = ScriptExport.ToPowerShell(@"C:\Program Files\llama\llama-server.exe",
            new[] { "-m", @"C:\models\it's.gguf", "--api-key", "***" }, @"C:\work dir",
            new Dictionary<string, string> { ["CUDA_VISIBLE_DEVICES"] = "0" }, "test");
        StringAssert.Contains(ps, "$env:CUDA_VISIBLE_DEVICES = '0'");
        StringAssert.Contains(ps, "Set-Location 'C:\\work dir'");
        StringAssert.Contains(ps, "& 'C:\\Program Files\\llama\\llama-server.exe'");
        StringAssert.Contains(ps, "'C:\\models\\it''s.gguf'");
        StringAssert.Contains(ps, "redacted");
    }

    [TestMethod]
    public void ScriptExport_Cmd_QuotesSpacesAndDoublesPercent()
    {
        var cmd = ScriptExport.ToCmd(@"C:\Program Files\llama\llama-server.exe", new[] { "-m", @"C:\m\a.gguf", "--alias", "100%" }, null, null);
        StringAssert.Contains(cmd, "\"C:\\Program Files\\llama\\llama-server.exe\"");
        StringAssert.Contains(cmd, @"C:\m\a.gguf");
        StringAssert.Contains(cmd, "100%%");
        Assert.IsFalse(cmd.Contains("redacted"));
    }
}
