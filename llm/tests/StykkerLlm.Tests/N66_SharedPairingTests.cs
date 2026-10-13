using Stykker.Pairing;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Die gemeinsame Kopplung (shared/Stykker.Pairing) in StykkerLLM: welche pairing.dat gilt, die Übernahme der alten
// Dateien und die Adapter AccessControl und HostRegistry. Alles in Temp-Ordnern, nie in %APPDATA%.
[TestClass]
public class N66_SharedPairingTests
{
    private static string TempDir(string tag) => Path.Combine(Path.GetTempPath(), "slm-n66-" + tag + "-" + Guid.NewGuid().ToString("N"));

    // ── Welche Ablage gilt ──

    [TestMethod]
    public void PairingFile_Default_IsTheSharedFileInAppData()
    {
        // Nur der Pfad wird berechnet: die Datei selbst wird nicht angelegt
        var paths = new AppPaths(TempDir("default"), isDefault: true);
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stykker", "pairing.dat"), paths.PairingFile());
        Assert.AreEqual(PairingStore.DefaultFile(), paths.PairingFile());
    }

    [TestMethod]
    public void PairingFile_DataDir_IsAPairingFileInsideTheDataDir()
    {
        var root = TempDir("dir");
        var paths = new AppPaths(root);
        Assert.IsFalse(paths.IsDefault);
        Assert.AreEqual(Path.Combine(root, "pairing.dat"), paths.PairingFile());
    }

    [TestMethod]
    public void PairingFile_Explicit_WinsOverDefaultAndDataDir()
    {
        var explicitFile = Path.Combine(TempDir("explicit"), "probe.dat");
        Assert.AreEqual(Path.GetFullPath(explicitFile), new AppPaths(TempDir("vorgabe"), isDefault: true).PairingFile(explicitFile));
        Assert.AreEqual(Path.GetFullPath(explicitFile), new AppPaths(TempDir("ordner")).PairingFile(explicitFile));
    }

    [TestMethod]
    public void PairingFile_BlankExplicit_IsIgnored()
    {
        var root = TempDir("blank");
        Assert.AreEqual(Path.Combine(root, "pairing.dat"), new AppPaths(root).PairingFile("   "));
    }
}
