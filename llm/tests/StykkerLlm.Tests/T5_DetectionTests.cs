using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// T5 – Nutzer-Report: "Er erkennt nicht, ob ein anderes Modell via llama.cpp läuft."
// Diese Tests fixieren das Verhalten der Erkennung für die gemeldeten Szenarien:
//  1) zwei parallele llama-server-Prozesse (verschiedene Modelle/Ports),
//  2) zweiter Prozess mit unlesbarer Kommandozeile (anderer Benutzer / erhöht),
//  3) Modellwechsel am selben Port bei einem llama.cpp-Fork, der nur per /props erkannt wird.
// Falls ein Szenario fehlschlägt, ist das ein gefundener Bug.
[TestClass]
public class T5_DetectionTests
{
    private const string Llama = "C:\\ai\\llama\\llama-server.exe";

    [TestMethod]
    public async Task TwoParallelLlamaServersAreBothListed()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows path semantics (drive letters, Windows folder)");
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        p.AddServer(30, Llama, "\"C:\\ai\\llama\\llama-server.exe\" -m C:\\m\\a.gguf --port 8081 --alias modell-a", 8081, start: 1);
        p.AddServer(31, Llama, "\"C:\\ai\\llama\\llama-server.exe\" -m C:\\m\\b.gguf --port 8082 --alias modell-b", 8082, start: 2);
        await reg.RefreshNowAsync();
        Assert.AreEqual(2, reg.Servers.Count, "beide Prozesse müssen erscheinen");
        var a = reg.Servers.Single(s => s.Info.Port == 8081);
        var b = reg.Servers.Single(s => s.Info.Port == 8082);
        Assert.AreEqual("modell-a", a.Name);
        Assert.AreEqual("modell-b", b.Name);
        Assert.AreEqual("a.gguf", Path.GetFileName(a.Info.Params?.Model));
        Assert.AreEqual("b.gguf", Path.GetFileName(b.Info.Params?.Model));
        Assert.AreEqual(30, a.Pid);
        Assert.AreEqual(31, b.Pid);
        reg.Dispose();
    }

    [TestMethod]
    public async Task SecondServerStartedLaterAppearsWithoutRestart()
    {
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        p.AddServer(30, Llama, "llama-server -m a.gguf --port 8081", 8081, start: 1);
        await reg.RefreshNowAsync();
        Assert.AreEqual(1, reg.Servers.Count);
        p.AddServer(31, Llama, "llama-server -m b.gguf --port 8090", 8090, start: 2);
        await reg.RefreshNowAsync();   // nächster Erkennungsdurchlauf, kein Monitor-Neustart
        Assert.AreEqual(2, reg.Servers.Count, "ein später gestarteter zweiter Server muss im nächsten Durchlauf erscheinen");
        Assert.IsTrue(reg.Servers.Any(s => s.Info.Port == 8090));
        reg.Dispose();
    }

    [TestMethod]
    public async Task SecondServerWithUnreadableCommandLineIsStillListed()
    {
        // Simuliert einen Prozess eines anderen Benutzers / erhöht gestartet: Name sichtbar, Kommandozeile nicht
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        p.AddServer(30, Llama, "\"C:\\ai\\llama\\llama-server.exe\" -m C:\\m\\a.gguf --port 8081", 8081, start: 1);
        p.AddServer(31, Llama, null, 8082, start: 2);   // keine Kommandozeile lesbar
        await reg.RefreshNowAsync();
        Assert.AreEqual(2, reg.Servers.Count, "auch ohne Kommandozeile muss der llama-server erscheinen (Erkennung über den Namen)");
        var hidden = reg.Servers.Single(s => s.Info.Port == 8082);
        Assert.IsFalse(hidden.Info.CommandLineReadable);
        Assert.AreEqual(SaveBlock.CommandLineUnreadable, hidden.Info.SaveBlock);
        Assert.IsTrue(hidden.Info.CanSave == false);
        reg.Dispose();
    }

    [TestMethod]
    public async Task ModelSwapOnSamePortUpdatesNameAndModel_ForPropDetectedFork()
    {
        // llama.cpp-Fork mit anderem exe-Namen, erkannt nur über /props; Modellwechsel = neuer PID, gleicher Port
        var handler = new FakeHandler();
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(handler));
        handler.Routes["127.0.0.1:8081/props"] = (HttpStatusCode.OK,
            "{\"model_path\":\"C:\\\\m\\\\first.gguf\",\"build_info\":\"b1\",\"total_slots\":1,\"model_alias\":\"first\",\"default_generation_settings\":{\"n_ctx\":4096}}");
        p.AddServer(40, "C:\\forks\\myserver.exe", null, 8081, start: 1);
        await reg.RefreshNowAsync();
        Assert.AreEqual(1, reg.Servers.Count);
        Assert.AreEqual("first", reg.Servers.Single().Name);

        handler.Routes["127.0.0.1:8081/props"] = (HttpStatusCode.OK,
            "{\"model_path\":\"C:\\\\m\\\\second.gguf\",\"build_info\":\"b1\",\"total_slots\":1,\"model_alias\":\"second\",\"default_generation_settings\":{\"n_ctx\":4096}}");
        p.Listeners.Clear();
        p.AddServer(41, "C:\\forks\\myserver.exe", null, 8081, start: 2);
        await reg.RefreshNowAsync();
        Assert.AreEqual(1, reg.Servers.Count, "derselbe Port bleibt ein Eintrag");
        Assert.AreEqual(41, reg.Servers.Single().Pid);
        Assert.AreEqual("second", reg.Servers.Single().Name, "nach dem Modellwechsel muss der neue Modellname angezeigt werden");
        reg.Dispose();
    }

    [TestMethod]
    public async Task RouterWithModelsDirIsListedAsRouter()
    {
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        p.AddServer(50, Llama, "llama-server --models-dir C:\\models --port 8091", 8091, start: 1);
        await reg.RefreshNowAsync();
        Assert.AreEqual(1, reg.Servers.Count);
        Assert.AreEqual(ServerMode.Router, reg.Servers.Single().Info.Mode);
        reg.Dispose();
    }

    [TestMethod]
    public void RelayProcessInWindowsFolderIsNeverProbed_ByDesign()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows path semantics (drive letters, Windows folder)");
        // Bekannte Grenze (kein Bug im engeren Sinn): llama.cpp in WSL2/Docker mit Port-Relay des Systems –
        // der Relay-Prozess liegt im Windows-Ordner und wird absichtlich nicht per /props befragt, damit
        // nicht jede Systemdienst-Port-Belegung angefragt wird. Solche Server erscheinen daher NICHT.
        var e = new ServerDiscovery.ProcEntry { Details = new ProcessDetails(99, 1, 4, @"C:\Windows\System32\wslrelay.exe", null, null, new Dictionary<string, string>()) };
        Assert.IsTrue(ServerDiscovery.IsExcludedFromProbe(e), "wslrelay.exe wird designbedingt nicht per Probe erkannt");
    }
}