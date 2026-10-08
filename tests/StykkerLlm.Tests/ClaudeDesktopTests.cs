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
        Assert.AreEqual("http://127.0.0.1:17500", (string?)node["inferenceGatewayBaseUrl"]);
        Assert.AreEqual("static", (string?)node["inferenceCredentialKind"]);
        Assert.AreEqual("bearer", (string?)node["inferenceGatewayAuthScheme"]);
        Assert.AreEqual(ClaudeDesktop.ModelAlias, (string?)((JsonArray)node["inferenceModels"]!)[0]);
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
