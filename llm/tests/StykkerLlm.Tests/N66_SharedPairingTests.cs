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
    public void FromDataDir_TheDefaultFolder_IsTheDefault_AndOtherFoldersAreNot()
    {
        // Das Fenster startet den Server mit --data-dir <Vorgabe>: auch dann gilt die gemeinsame Ablage (ohne Migration)
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StykkerLLM");
        var asDefault = AppPaths.FromDataDir(defaultRoot + Path.DirectorySeparatorChar);
        Assert.IsTrue(asDefault.IsDefault);
        Assert.AreEqual(PairingStore.DefaultFile(), asDefault.PairingFile());

        var other = AppPaths.FromDataDir(TempDir("other"));
        Assert.IsFalse(other.IsDefault);
        Assert.AreEqual(Path.Combine(other.Root, "pairing.dat"), other.PairingFile());
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

    // ── Übernahme der alten Dateien (access.dat, hosts.dat) ──

    // Eine access.dat wie StykkerLLM sie vor der gemeinsamen Ablage geschrieben hat (Klartext, wie unter Linux)
    private static void WriteLegacyAccess(AppPaths paths, string devicesJson, string code = "", bool remote = false) =>
        File.WriteAllText(Path.Combine(paths.Root, "access.dat"),
            $$"""{ "RemoteEnabled": {{(remote ? "true" : "false")}}, "Code": "{{code}}", "CodeCreated": "2026-10-06T10:00:00", "Devices": [ {{devicesJson}} ] }""");

    private static string LegacyDevice(string id, string name, string hash, string? role)
    {
        var roleJson = role == null ? "" : ", \"Role\": \"" + role + "\"";
        return "{ \"Id\": \"" + id + "\", \"Name\": \"" + name + "\", \"Created\": \"2026-10-06T10:00:00\", \"Hash\": \"" + hash + "\"" + roleJson + " }";
    }

    private static void WriteLegacyHosts(AppPaths paths, string id, string name, string tokenHash) =>
        File.WriteAllText(Path.Combine(paths.Root, "hosts.dat"),
            $$"""{ "Hosts": [ { "Id": "{{id}}", "Name": "{{name}}", "TokenHash": "{{tokenHash}}", "Added": "2026-10-06T10:00:00" } ] }""");

    private static bool Exists(AppPaths paths, string file) => File.Exists(Path.Combine(paths.Root, file));

    [TestMethod]
    public void LegacyAccess_DevicesStaySignedIn_WithTheirOldRole_AndTheFileBecomesBak()
    {
        var paths = NewPaths("legacy-dev");
        WriteLegacyAccess(paths, LegacyDevice("altadmin", "Handy A", AccessControl.Hash("alter-admin"), "admin") + ","
            + LegacyDevice("altview", "Handy B", AccessControl.Hash("alter-viewer"), "viewer"));

        var access = new AccessControl(paths, new FakePlatform());
        Assert.AreEqual(AccessRole.Admin, access.RoleOf("alter-admin"));
        Assert.AreEqual(AccessRole.Viewer, access.RoleOf("alter-viewer"));
        Assert.IsTrue(Exists(paths, "access.dat.bak"));
        Assert.IsFalse(Exists(paths, "access.dat"));
    }

    [TestMethod]
    public void LegacyHosts_FindTheHostWithItsOldToken_AndTheFileBecomesBak()
    {
        var paths = NewPaths("legacy-host");
        WriteLegacyHosts(paths, "altbox01", "gpu-box", AccessControl.Hash("alter-host-token"));

        var registry = new HostRegistry(paths, new FakePlatform());
        Assert.AreEqual("gpu-box", registry.Find("alter-host-token")?.Name);
        Assert.AreEqual("altbox01", registry.Find("alter-host-token")?.Id, "die alte Id bleibt");
        Assert.IsTrue(Exists(paths, "hosts.dat.bak"));
        Assert.IsFalse(Exists(paths, "hosts.dat"));
    }

    [TestMethod]
    public void SecondStart_ImportsNothingTwice_EvenIfTheOldFileComesBack()
    {
        var paths = NewPaths("twice");
        WriteLegacyAccess(paths, LegacyDevice("altadmin", "Handy", AccessControl.Hash("t1"), "admin"));
        WriteLegacyHosts(paths, "altbox01", "box", AccessControl.Hash("h1"));
        _ = new AccessControl(paths, new FakePlatform());
        Assert.AreEqual(1, new AccessControl(paths, new FakePlatform()).DeviceCount);

        // Die alte Datei kommt zurück (jemand hat sie aus dem .bak geholt): noch einmal starten
        File.Move(Path.Combine(paths.Root, "access.dat.bak"), Path.Combine(paths.Root, "access.dat"));
        var again = new AccessControl(paths, new FakePlatform());
        Assert.AreEqual(1, again.DeviceCount, "das Gerät steht nicht doppelt drin");
        Assert.AreEqual(1, new HostRegistry(paths, new FakePlatform()).List().Count, "der Host auch nicht");
        Assert.AreEqual(2, new PairingStore(paths.PairingFile()).Read().Imported.Count);
    }

    [TestMethod]
    public void DeviceRemovedAfterTheImport_StaysRemoved_EvenIfTheBakComesBack()
    {
        var paths = NewPaths("legacy-removed");
        WriteLegacyAccess(paths, LegacyDevice("altrem", "Altes Handy", AccessControl.Hash("alt-cookie"), "admin"));
        var access = new AccessControl(paths, new FakePlatform());
        Assert.IsNotNull(access.RoleOf("alt-cookie"));
        Assert.IsTrue(access.RemoveDevice("altrem"));

        File.Move(Path.Combine(paths.Root, "access.dat.bak"), Path.Combine(paths.Root, "access.dat"));
        var again = new AccessControl(paths, new FakePlatform());
        Assert.IsNull(again.RoleOf("alt-cookie"), "das Gerät kommt nicht aus der alten Datei zurück");
        Assert.AreEqual(0, again.DeviceCount);
    }

    [TestMethod]
    public void LegacyRoles_MissingIsAdmin_HubIsViewer()
    {
        var paths = NewPaths("legacy-roles");
        WriteLegacyAccess(paths, LegacyDevice("vorrollen", "Alt", AccessControl.Hash("tok-alt"), null) + ","
            + LegacyDevice("hubdev", "Hub", AccessControl.Hash("tok-hub"), "hub"));

        var access = new AccessControl(paths, new FakePlatform());
        Assert.AreEqual(AccessRole.Admin, access.RoleOf("tok-alt"), "ohne Rollenfeld: Admin");
        Assert.AreEqual(AccessRole.Viewer, access.RoleOf("tok-hub"), "ein alter Hub: nur Viewer");
    }

    [TestMethod]
    public void ExistingPairingFile_KeepsItsDevicesAndCode_LegacyAddsOnly()
    {
        var paths = NewPaths("merge");
        var first = new AccessControl(paths, new FakePlatform());
        first.SetRemote(false);
        // Die Zentrale hat schon ein Gerät gekoppelt (Rolle in "hud"), und der Schalter ist aus
        var hub = OtherModule(paths);
        var hubDevice = hub.Pair(first.Code, "Zentrale-Handy", null, "hud")!.Value;
        var code = first.Code;   // der Code nach der Kopplung gilt, der alte ist verbraucht

        // Die alte access.dat hat einen anderen Code, einen Schalter an und ein eigenes Gerät
        WriteLegacyAccess(paths, LegacyDevice("altdev", "Altes Handy", AccessControl.Hash("alt-tok"), "admin"), code: "654321", remote: true);
        var second = new AccessControl(paths, new FakePlatform());

        Assert.AreEqual(code, second.Code, "der Code der Ablage bleibt, der alte wird nicht übernommen");
        Assert.IsTrue(second.RemoteEnabled, "ein eingeschalteter Schalter bleibt an");
        Assert.AreEqual(AccessRole.Admin, hub.RoleOf(hubDevice.Token, "hud"), "das Gerät der Zentrale bleibt");
        Assert.AreEqual(2, second.DeviceCount, "Zentrale und altes Gerät");
        Assert.AreEqual(AccessRole.Admin, second.RoleOf("alt-tok"));
    }

    [TestMethod]
    public void DevicePairedInLlm_IsViewerInAnotherModule_ForASecondService()
    {
        var paths = NewPaths("llm-admin");
        var access = new AccessControl(paths, new FakePlatform());
        var paired = access.Pair(access.Code, "Handy", null)!.Value;
        Assert.AreEqual(AccessRole.Admin, access.RoleOf(paired.Token));
        Assert.AreEqual(PairingRoles.Viewer, OtherModule(paths).RoleOf(paired.Token, "hud"));
    }

    [TestMethod]
    public void UnreadableLegacyFile_StaysWhereItIs_AndNoBakIsMade()
    {
        var paths = NewPaths("unreadable");
        File.WriteAllText(Path.Combine(paths.Root, "access.dat"), "kein gültiges json {{{");

        _ = new AccessControl(paths, new FakePlatform());
        Assert.IsTrue(Exists(paths, "access.dat"), "die Datei bleibt liegen");
        Assert.IsFalse(Exists(paths, "access.dat.bak"));
    }
}
