using System.Text.Json.Nodes;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Der Schalter „Claude Desktop auf den Stykker-Proxy": schreibt in die 3P-Ablage der App (%LOCALAPPDATA%\Claude-3p)
// einen Gateway-Eintrag und wendet ihn an; Ausschalten stellt das vorherige Ziel wieder her. Beendet werden nur
// Prozesse aus dem MSIX-Installationsordner der App, nie Claude Code.
[TestClass]
public class ClaudeDesktopTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LibDir(string dir) => Path.Combine(dir, "configLibrary");

    [TestMethod]
    public void Enable_WritesGatewayEntryAndApplies()
    {
        var data = TempDir();
        var layer = TempDir();
        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer);
        Assert.IsTrue(cd.Supported);
        Assert.IsFalse(cd.Enabled);

        var msg = cd.Enable(out var error);

        Assert.IsNull(error, error);
        Assert.IsTrue(cd.Enabled);
        StringAssert.Contains(msg, "http://127.0.0.1:17500");

        var entry = Directory.GetFiles(LibDir(layer), "*.json").Single(f => !f.EndsWith("_meta.json", StringComparison.Ordinal));
        var node = JsonNode.Parse(File.ReadAllText(entry))!;
        Assert.AreEqual("gateway", (string?)node["inferenceProvider"]);
        Assert.AreEqual("http://127.0.0.1:17500/v1/messages", (string?)node["inferenceGatewayBaseUrl"]);
        Assert.AreEqual("static", (string?)node["inferenceCredentialKind"]);
        Assert.AreEqual("bearer", (string?)node["inferenceGatewayAuthScheme"]);
        Assert.AreEqual(ClaudeDesktop.ModelAlias, (string?)((JsonArray)node["inferenceModels"]!)[0]!["name"]);
        Assert.AreEqual("stykker", (string?)node["inferenceGatewayApiKey"]);

        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(LibDir(layer), "_meta.json")))!;
        Assert.AreEqual(Path.GetFileNameWithoutExtension(entry), (string?)meta["appliedId"]);
        Assert.AreEqual(1, ((JsonArray)meta["entries"]!).Count);
    }

    [TestMethod]
    public void Disable_RestoresPreviousAppliedEntry()
    {
        var data = TempDir();
        var layer = TempDir();
        // Der Nutzer hatte schon einen Eintrag, der angewendet war
        Directory.CreateDirectory(LibDir(layer));
        var other = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(LibDir(layer), other + ".json"), "{\"inferenceProvider\":\"anthropic\"}");
        File.WriteAllText(Path.Combine(LibDir(layer), "_meta.json"),
            $"{{\"appliedId\":\"{other}\",\"entries\":[{{\"id\":\"{other}\",\"name\":\"Default\"}}]}}");

        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer);
        cd.Enable(out var error);
        Assert.IsNull(error, error);
        Assert.IsTrue(cd.Enabled);

        cd.Disable(out error);

        Assert.IsNull(error, error);
        Assert.IsFalse(cd.Enabled);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(LibDir(layer), "_meta.json")))!;
        Assert.AreEqual(other, (string?)meta["appliedId"]);
        Assert.AreEqual(1, ((JsonArray)meta["entries"]!).Count);
        Assert.AreEqual(1, Directory.GetFiles(LibDir(layer), "*.json").Count(f => !f.EndsWith("_meta.json", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Disable_RemovesTheLibraryAgain_WhenEnableCreatedIt()
    {
        var data = TempDir();
        var layer = TempDir();
        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer);
        cd.Enable(out var error);
        Assert.IsNull(error, error);

        cd.Disable(out error);

        Assert.IsNull(error, error);
        Assert.IsFalse(cd.Enabled);
        Assert.IsFalse(File.Exists(Path.Combine(LibDir(layer), "_meta.json")), "die Ablage war vorher nicht da");
        Assert.IsFalse(Directory.Exists(LibDir(layer)));
    }

    [TestMethod]
    public void Disable_AppliesNoOtherEntry_WhenNoneWasAppliedBefore()
    {
        var data = TempDir();
        var layer = TempDir();
        // Eine Bibliothek mit einem Eintrag, aber nichts angewendet: die App sprach direkt mit Anthropic
        Directory.CreateDirectory(LibDir(layer));
        var other = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(LibDir(layer), other + ".json"), "{\"inferenceProvider\":\"gateway\"}");
        File.WriteAllText(Path.Combine(LibDir(layer), "_meta.json"), $"{{\"entries\":[{{\"id\":\"{other}\",\"name\":\"Other\"}}]}}");

        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer);
        cd.Enable(out var error);
        Assert.IsNull(error, error);
        cd.Disable(out error);

        Assert.IsNull(error, error);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(LibDir(layer), "_meta.json")))!;
        Assert.IsNull(meta["appliedId"], "kein anderer Eintrag wird angewendet");
        Assert.AreEqual(1, ((JsonArray)meta["entries"]!).Count);
        Assert.IsTrue(File.Exists(Path.Combine(LibDir(layer), other + ".json")));
    }

    [TestMethod]
    public void Disable_WithoutStateFile_FindsOurEntryByName()
    {
        var data = TempDir();
        var layer = TempDir();
        new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer).Enable(out var error);
        Assert.IsNull(error, error);
        File.Delete(Path.Combine(data, "claude-desktop.json"));

        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer);
        Assert.IsTrue(cd.Enabled, "am Namen erkannt");
        cd.Disable(out error);

        Assert.IsNull(error, error);
        Assert.IsFalse(cd.Enabled);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(LibDir(layer), "_meta.json")))!;
        Assert.IsNull(meta["appliedId"]);
        Assert.AreEqual(0, ((JsonArray)meta["entries"]!).Count);
        Assert.AreEqual(0, Directory.GetFiles(LibDir(layer), "*.json").Count(f => !f.EndsWith("_meta.json", StringComparison.Ordinal)));
    }

    // Ein von Hand angelegtes Gateway auf diesem Rechner (wie „Strata“), so wie die App es selbst schreibt
    private static string WriteLocalGateway(string layer, string name, bool applied)
    {
        Directory.CreateDirectory(LibDir(layer));
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(LibDir(layer), id + ".json"),
            "{\"inferenceGatewayBaseUrl\":\"http://127.0.0.1:8082/v1/messages\",\"inferenceModels\":[{\"name\":\"claude-stykker\"}],\"inferenceProvider\":\"gateway\",\"inferenceCredentialKind\":\"static\"}");
        var appliedPart = applied ? ",\"appliedId\":\"" + id + "\"" : "";
        File.WriteAllText(Path.Combine(LibDir(layer), "_meta.json"),
            "{\"entries\":[{\"id\":\"" + id + "\",\"name\":\"" + name + "\"}]" + appliedPart + "}");
        return id;
    }

    [TestMethod]
    public void A_manual_local_gateway_counts_as_on_and_switching_off_unapplies_it()
    {
        var data = TempDir();
        var layer = TempDir();
        var strata = WriteLocalGateway(layer, "Strata", applied: true);

        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer);
        Assert.IsTrue(cd.Enabled, "ein lokales Gateway gilt als eingeschaltet");
        cd.Disable(out var error);

        Assert.IsNull(error, error);
        Assert.IsFalse(cd.Enabled);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(LibDir(layer), "_meta.json")))!;
        Assert.IsNull(meta["appliedId"], "zurück auf Anthropic");
        Assert.IsTrue(File.Exists(Path.Combine(LibDir(layer), strata + ".json")), "der Eintrag bleibt, er wird nur nicht angewendet");
    }

    [TestMethod]
    public void Switching_off_never_restores_a_local_gateway_as_the_previous_target()
    {
        var data = TempDir();
        var layer = TempDir();
        WriteLocalGateway(layer, "Strata", applied: true);

        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, data, layer);
        cd.Enable(out var error);
        Assert.IsNull(error, error);
        cd.Disable(out error);

        Assert.IsNull(error, error);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(LibDir(layer), "_meta.json")))!;
        Assert.IsNull(meta["appliedId"]);
        Assert.IsFalse(cd.Enabled);
    }

    [TestMethod]
    public void Running_CountsOnlyAppProcesses()
    {
        var plat = new FakePlatform();
        plat.AddServer(5000, @"C:\Program Files\WindowsApps\Claude_2.26454.2.0_x64__pzs8sxrjxfjjc\app\claude.exe", null, 0);
        plat.AddServer(5001, @"C:\Users\x\AppData\Roaming\Claude\claude-code\2.1.29\claude.exe", null, 0);
        var cd = new ClaudeDesktop(plat, () => 17500, TempDir(), TempDir());

        cd.Maintain(DateTime.Now);

        Assert.AreEqual(1, cd.RunningCount, "nur die App aus WindowsApps zählt, nicht Claude Code");
    }

    [TestMethod]
    public void CloseRunning_EndsOnlyAppProcesses()
    {
        var plat = new FakePlatform();
        plat.AddServer(5000, @"C:\Program Files\WindowsApps\Claude_2.26454.2.0_x64__pzs8sxrjxfjjc\app\claude.exe", null, 0);
        plat.AddServer(5001, @"C:\Users\x\AppData\Roaming\Claude\claude-code\2.1.29\claude.exe", null, 0);
        var cd = new ClaudeDesktop(plat, () => 17500, TempDir(), TempDir());

        int closed = cd.CloseRunning(out var error);

        Assert.IsNull(error, error);
        Assert.AreEqual(1, closed);
        Assert.IsTrue(plat.Terminated.Any(t => t.Pid == 5000));
        Assert.IsFalse(plat.Terminated.Any(t => t.Pid == 5001), "Claude Code wird nie beendet");
    }

    [TestMethod]
    public void Unsupported_ReportsError()
    {
        var cd = new ClaudeDesktop(new FakePlatform(), () => 17500, TempDir(), "");

        Assert.IsFalse(cd.Supported);
        cd.Enable(out var error);

        Assert.AreEqual(Strings.ClaudeUnsupported, error);
        Assert.IsFalse(cd.Enabled);
    }
}
