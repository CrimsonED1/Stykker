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

    // ── AccessControl als Adapter auf die gemeinsame Ablage ──

    private static AppPaths NewPaths(string tag)
    {
        var paths = new AppPaths(TempDir(tag));
        Directory.CreateDirectory(paths.Root);
        return paths;
    }

    // Die Zentrale (HUD) oder SYS: ein eigener Dienst auf derselbe Datei, so wie ein anderer Prozess ihn hätte
    private static PairingService OtherModule(AppPaths paths) => new(new PairingStore(paths.PairingFile()));

    [TestMethod]
    public void RemovalInAnotherProcess_IsSeenAtOnce()
    {
        var paths = NewPaths("foreign");
        var server = new AccessControl(paths, new FakePlatform());
        var other = new AccessControl(paths, new FakePlatform());
        var token = server.Pair(server.Code, "Handy", "10.0.0.5")!.Value.Token;
        Assert.AreEqual(AccessRole.Admin, other.RoleOf(token));

        // Die andere Sitzung entfernt das Gerät: der Server weist es beim nächsten Zugriff ab
        Assert.IsTrue(other.RemoveDevice(server.Devices.Single().Id));
        Assert.IsNull(server.RoleOf(token), "ohne Neustart abgewiesen");
    }

    [TestMethod]
    public void DeviceCoupledInAnotherModule_IsViewerHere()
    {
        var paths = NewPaths("hub-device");
        var access = new AccessControl(paths, new FakePlatform());
        var hub = OtherModule(paths);

        // Die Zentrale koppelt das Handy (Rolle in "hud"): in LLM ist es Viewer, aber angemeldet
        var paired = hub.Pair(access.Code, "Handy", null, "hud");
        Assert.IsNotNull(paired);
        Assert.AreEqual(AccessRole.Viewer, access.RoleOf(paired.Value.Token), "ein anderes Modul sieht nur zu");
        Assert.AreEqual(AccessRole.Viewer, access.Devices.Single().Role);
        Assert.AreEqual(1, access.DeviceCount);
    }

    [TestMethod]
    public void RoleChangedInLlm_LeavesOtherModulesAlone()
    {
        var paths = NewPaths("roles");
        var access = new AccessControl(paths, new FakePlatform());
        var hub = OtherModule(paths);
        var paired = hub.Pair(access.Code, "Handy", null, "hud")!.Value;
        var id = access.Devices.Single().Id;

        Assert.IsTrue(access.SetRole(id, AccessRole.Admin));
        Assert.AreEqual(AccessRole.Admin, access.RoleOf(paired.Token), "in LLM jetzt Admin");
        Assert.AreEqual(PairingRoles.Admin, hub.RoleOf(paired.Token, "hud"), "in der Zentrale unverändert");
        Assert.IsTrue(access.SetRole(id, AccessRole.Viewer));
        Assert.AreEqual(PairingRoles.Admin, hub.RoleOf(paired.Token, "hud"), "auch nach dem Zurücksetzen");
    }

    [TestMethod]
    public void Times_RoundTrip_ForAnUnzonedClock()
    {
        var clock = new DateTime(2026, 10, 6, 12, 0, 0);   // ohne Angabe der Zone: Ortszeit
        var paths = NewPaths("times");
        var access = new AccessControl(paths, new FakePlatform(), () => clock);
        Assert.AreEqual(clock + AccessControl.CodeLifetime, access.CodeExpires);
        var added = access.AddLocalDevice("Fenster", null)!.Value;
        Assert.AreEqual(clock, added.Device.Created, "Erstellt kommt in Ortszeit zurück");
        Assert.AreEqual(clock, added.Device.LastSeen);
    }

    [TestMethod]
    public void Host_AddedHere_IsFoundByAnotherService_AndIsNotADevice()
    {
        var paths = NewPaths("host");
        var registry = new HostRegistry(paths, new FakePlatform());
        var (entry, token) = registry.Add("gpu-box", DateTime.Now);

        var found = OtherModule(paths).FindHost(token);
        Assert.IsNotNull(found, "der Host steht in der gemeinsamen Ablage");
        Assert.AreEqual("gpu-box", found.Name);
        // Hosts haben keine Rollen und sind keine Browser-Geräte
        Assert.AreEqual(0, new AccessControl(paths, new FakePlatform()).DeviceCount);
        Assert.AreEqual(entry.Id, registry.Find(token)?.Id);
    }

    [TestMethod]
    public void PairingFile_IsNotPlainJson_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;   // außerhalb von Windows bleibt die Datei Klartext (wie vorher)
        var paths = NewPaths("protected");
        var access = new AccessControl(paths, new FakePlatform());
        _ = access.Code;
        var raw = File.ReadAllText(paths.PairingFile()).TrimStart();
        Assert.IsFalse(raw.StartsWith('{'), "die Ablage ist mit dem Benutzerschutz verschlüsselt");
    }
}
