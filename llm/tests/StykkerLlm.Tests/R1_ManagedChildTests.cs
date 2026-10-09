using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// R1.1 – LM Studio startet beim Laden eines Modells einen eigenen llama-server (Kindprozess mit Zufallsport und --api-key).
// Der gehört zur LM-Studio-Karte (VRAM, Slots) und ist keine eigene Karte mit Stop und Save. Ollama-Kinder verschwinden ganz.
[TestClass]
public class R1_ManagedChildTests
{
    private const string LmExe = @"C:\Program Files\LM Studio\LM Studio.exe";
    private const string LmEngine = @"C:\Users\x\LMStudio\extensions\backends\llama.cpp-win\llama-server.exe";
    private const string EngineCmd = "\"" + LmEngine + "\" --model C:\\m\\gemma-4-E4B-it-Q8_0.gguf --host 127.0.0.1 --port 53303 --api-key GEHEIM123 --ctx-size 4096 --parallel 2";
    private const string Slots = "[{\"id\":0,\"n_ctx\":4096,\"is_processing\":false},{\"id\":1,\"n_ctx\":4096,\"is_processing\":false}]";

    private static FakeHandler LmHandler()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:1234/api/v0/models"] = (HttpStatusCode.OK, BackendSamples.LmModels);
        h.Routes["127.0.0.1:53303/slots"] = (HttpStatusCode.OK, Slots);
        h.Routes["127.0.0.1:53303/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        h.Routes["127.0.0.1:53303/v1/models"] = (HttpStatusCode.OK, "{\"data\":[{\"id\":\"gemma\"}]}");
        return h;
    }

    private static FakePlatform LmPlatform(int engineParent = 100)
    {
        var p = new FakePlatform();
        p.AddServer(100, LmExe, null, 1234, "127.0.0.1", parent: 50);
        p.AddServer(101, LmEngine, EngineCmd, 53303, "127.0.0.1", parent: engineParent, start: 5);
        p.Processes[50] = new ProcessDetails(50, 1, 4, @"C:\Windows\explorer.exe", null, null, new Dictionary<string, string>());
        return p;
    }

    [TestMethod]
    public async Task LmStudioEngineChild_IsNotAnOwnCard_ButAttachedToLmStudio()
    {
        var reg = new ServerRegistry(LmPlatform(), new HttpClient(LmHandler()));
        await reg.RefreshNowAsync();
        var s = reg.Servers.Single();
        Assert.AreEqual(BackendKind.LmStudio, s.Kind);
        Assert.AreEqual(1234, s.Info.Port);
        Assert.AreEqual(SaveBlock.OtherBackend, s.Info.SaveBlock);
        var kid = s.Info.Children.Single();
        Assert.AreEqual(101, kid.Pid);
        Assert.AreEqual(53303, kid.Port);
        Assert.AreEqual("C:\\m\\gemma-4-E4B-it-Q8_0.gguf", kid.ModelPath);
        Assert.AreEqual("GEHEIM123", kid.ApiKey?.Reveal());
        Assert.AreEqual(1, s.Children.Count);
        reg.Dispose();
    }

    [TestMethod]
    public async Task EngineBelowAnotherProcess_GrandchildIsAttachedToo()
    {
        var p = LmPlatform(engineParent: 102);
        p.Processes[102] = new ProcessDetails(102, 3, 100, @"C:\Program Files\LM Studio\resources\node.exe", null, null, new Dictionary<string, string>());
        var reg = new ServerRegistry(p, new HttpClient(LmHandler()));
        await reg.RefreshNowAsync();
        var s = reg.Servers.Single();
        Assert.AreEqual(1, s.Info.Children.Count, "Großelternprozess LM Studio reicht");
        reg.Dispose();
    }

    [TestMethod]
    public async Task EngineWithoutLmStudioServer_StaysHidden()
    {
        // LM Studio ohne aktiven API-Server: kein Listener des Programms, die Engine darf trotzdem keine llama.cpp-Karte werden
        var p = new FakePlatform();
        p.Processes[100] = new ProcessDetails(100, 1, 50, LmExe, null, null, new Dictionary<string, string>());
        p.AddServer(101, LmEngine, EngineCmd, 53303, "127.0.0.1", parent: 100, start: 5);
        var reg = new ServerRegistry(p, new HttpClient(LmHandler()));
        await reg.RefreshNowAsync();
        Assert.AreEqual(0, reg.Servers.Count);
        reg.Dispose();
    }

    [TestMethod]
    public async Task OllamaChildLlamaServer_IsNotListed()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:11434/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        h.Routes["127.0.0.1:11434/api/ps"] = (HttpStatusCode.OK, "{\"models\":[]}");
        var p = new FakePlatform();
        p.AddServer(200, @"C:\Ollama\ollama.exe", "ollama.exe serve", 11434, "127.0.0.1");
        p.AddServer(201, @"C:\Ollama\lib\llama-server.exe", "llama-server --port 50123", 50123, "127.0.0.1", parent: 200, start: 5);
        var reg = new ServerRegistry(p, new HttpClient(h));
        await reg.RefreshNowAsync();
        Assert.AreEqual(BackendKind.Ollama, reg.Servers.Single().Kind);
        reg.Dispose();
    }

    [TestMethod]
    public async Task OllamaTrayApp_UiPortIsNotAnOllamaServer()
    {
        // live gefunden: "ollama app.exe" lauscht auf einem Zufallsport (Oberfläche), der /api/version beantwortet
        var h = new FakeHandler();
        h.Routes["127.0.0.1:11434/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        h.Routes["127.0.0.1:11434/api/ps"] = (HttpStatusCode.OK, "{\"models\":[]}");
        h.Routes["127.0.0.1:53179/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        var p = new FakePlatform();
        p.AddServer(200, @"C:\Ollama\ollama.exe", "ollama.exe serve", 11434, "127.0.0.1", parent: 199);
        p.AddServer(199, @"C:\Ollama\ollama app.exe", null, 53179, "127.0.0.1");
        var reg = new ServerRegistry(p, new HttpClient(h));
        await reg.RefreshNowAsync();
        Assert.AreEqual(11434, reg.Servers.Single().Info.Port);
        reg.Dispose();
    }

    [TestMethod]
    public async Task OwnLlamaServer_WithOrdinaryParent_IsStillListed()
    {
        var p = new FakePlatform();
        p.Processes[50] = new ProcessDetails(50, 1, 4, @"C:\Windows\System32\cmd.exe", null, null, new Dictionary<string, string>());
        p.AddServer(300, @"C:\llama\llama-server.exe", "llama-server -m a.gguf --port 8081", 8081, parent: 50);
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        await reg.RefreshNowAsync();
        Assert.AreEqual(BackendKind.LlamaCpp, reg.Servers.Single().Kind);
        reg.Dispose();
    }

    [TestMethod]
    public async Task TwoLmStudioServers_EachEngineGoesToItsOwnParent()
    {
        var h = LmHandler();
        h.Routes["127.0.0.1:1235/api/v0/models"] = (HttpStatusCode.OK, BackendSamples.LmEmpty);
        var p = LmPlatform();
        p.AddServer(110, LmExe, null, 1235, "127.0.0.1", parent: 50);
        var reg = new ServerRegistry(p, new HttpClient(h));
        await reg.RefreshNowAsync();
        Assert.AreEqual(2, reg.Servers.Count);
        Assert.AreEqual(1, reg.Servers.Single(s => s.Info.Port == 1234).Info.Children.Count);
        Assert.AreEqual(0, reg.Servers.Single(s => s.Info.Port == 1235).Info.Children.Count);
        reg.Dispose();
    }

    [TestMethod]
    public async Task LmStudioCard_ShowsEngineVramSlotsAndUsesChildKey()
    {
        var h = LmHandler();
        var p = LmPlatform();
        p.GpuMem[101] = (6.2, 0.1);
        var reg = new ServerRegistry(p, new HttpClient(h));
        await reg.RefreshNowAsync();
        var w = reg.Servers.Single();
        double? vram = null;
        for (int i = 0; i < 60 && vram == null; i++)
        {
            await w.PollAsync();
            await Task.Delay(100);
            vram = w.VramGb;
        }
        Assert.IsNotNull(vram, "VRAM der Engine muss in der LM-Studio-Karte stehen");
        Assert.AreEqual(6.2, vram!.Value, 0.001);
        Assert.AreEqual(2, w.Children.Single().Slots.Count);
        Assert.IsTrue(h.AuthHeaders.Contains("Bearer GEHEIM123"), "die Engine wird mit ihrem eigenen Schlüssel befragt");
        reg.Dispose();
    }

    [TestMethod]
    public async Task EngineChild_NeverReachesLibraryOrCommandLineCopies()
    {
        var lib = new Library();
        var reg = new ServerRegistry(LmPlatform(), new HttpClient(LmHandler()), lib);
        await reg.RefreshNowAsync();
        reg.ObserveLibrary(DateTime.Now);
        Assert.AreEqual(0, lib.History.Count);
        var s = reg.Servers.Single();
        Assert.IsFalse(s.Info.Children.Any(c => (c.ApiKey?.ToString() ?? "").Contains("GEHEIM")), "ToString des Schlüssels bleibt geschwärzt");
        reg.Dispose();
    }
}
