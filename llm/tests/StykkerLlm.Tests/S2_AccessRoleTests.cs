using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Rollen der angemeldeten Geräte (docs/architecture.md, „Rollen später: Viewer / Admin“):
// ein Viewer-Gerät darf alles lesen, aber nichts verändern – und bekommt den Zugangscode nicht zu sehen,
// sonst könnte es sich damit als Admin anmelden und die Rolle umgehen.
[TestClass]
public class S2_AccessRoleTests
{
    private static AppPaths TempPaths(string tag) => new(Path.Combine(Path.GetTempPath(), "slm-role-" + tag + "-" + Guid.NewGuid().ToString("N")));

    [TestMethod]
    public void Role_ANewDeviceIsAdmin_AndAnOldFileWithoutRoleIsAdminToo()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var platform = new FakePlatform();          // ohne UserProtection steht access.dat als JSON da
        var access = new AccessControl(paths, platform);

        var paired = access.Pair(access.Code, "Handy", "10.0.0.5")!.Value;
        Assert.AreEqual(AccessRole.Admin, paired.Device.Role, "neu angemeldet = Admin");
        Assert.AreEqual(AccessRole.Admin, access.RoleOf(paired.Token, null));

        // Eine access.dat von vor den Rollen (Feld "role" fehlt) darf niemanden aussperren
        var old = Path.Combine(tmp.Path, "old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, AccessControl.FileName), """
            { "RemoteEnabled": true, "Code": "ABCD2345", "Devices": [ { "Id": "kalt", "Name": "alt", "Hash": "abc" } ] }
            """);
        var legacy = new AccessControl(new AppPaths(old), platform);
        Assert.AreEqual(1, legacy.Devices.Count);
        Assert.AreEqual(AccessRole.Admin, legacy.Devices[0].Role);

        // Ein erfundener Rollenwert landet auch bei Admin, nicht bei einem dritten Zustand
        var d = new AccessDevice { Role = "superuser" };
        Assert.AreEqual(AccessRole.Admin, d.Role);
        Assert.IsFalse(AccessRole.IsValid("superuser"));
        Assert.IsFalse(AccessRole.CanWrite(AccessRole.Viewer));
        Assert.IsTrue(AccessRole.CanWrite(null));            // Schlüssel aus dem Datenordner = Admin
    }

    [TestMethod]
    public void Role_SetRolePersistsAndRejectsWhatItDoesNotKnow()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var platform = new FakePlatform { UserProtection = true };
        var access = new AccessControl(paths, platform);
        var paired = access.Pair(access.Code, "Handy", "10.0.0.5")!.Value;

        Assert.IsTrue(access.SetRole(paired.Device.Id, AccessRole.Viewer));
        Assert.AreEqual(AccessRole.Viewer, access.RoleOf(paired.Token, null));
        Assert.IsTrue(access.Validate(paired.Token, null), "die Anmeldung gilt trotzdem");

        // Zweite Instanz (Fenster, TUI, Web) sieht die Rolle
        var again = new AccessControl(paths, platform);
        Assert.AreEqual(AccessRole.Viewer, again.Devices[0].Role);
        Assert.AreEqual(AccessRole.Viewer, again.RoleOf(paired.Token, null));

        // Unbekanntes Gerät und unbekannte Rolle werden abgewiesen, eine falsche Rolle überschreibt nichts
        Assert.IsFalse(access.SetRole("gibtsnicht", AccessRole.Viewer));
        Assert.IsFalse(access.SetRole(paired.Device.Id, "halfadmin"));
        Assert.AreEqual(AccessRole.Viewer, access.Devices[0].Role);
        // Gleiche Rolle noch einmal: Erfolg, damit die Aktion wiederholbar bleibt
        Assert.IsTrue(access.SetRole(paired.Device.Id, AccessRole.Viewer));
        Assert.IsTrue(access.SetRole(paired.Device.Id, AccessRole.Admin));
        Assert.AreEqual(AccessRole.Admin, access.Devices[0].Role);
    }

    [TestMethod]
    public async Task Role_StateCarriesTheRole_AndTheCodeOnlyForAdmins()
    {
        using var host = new SimHost(new List<SimServerSpec>(), seed: 3, autoStep: false);
        var paths = TempPaths("state");
        Directory.CreateDirectory(paths.Root);
        try
        {
            var access = new AccessControl(paths, new FakePlatform());
            access.SetRemote(true);
            var paired = access.Pair(access.Code, "Handy", "10.0.0.5")!.Value;
            access.SetRole(paired.Device.Id, AccessRole.Viewer);

            // Admin (Fenster, TUI, Skript mit dem Schlüssel): alles im Zustand
            var admin = StateSnapshot.Parse(StateJson.WriteText(host.Engine, access, null, 8078, DateTimeOffset.Now));
            Assert.AreEqual(access.Code, admin.Access.Code);
            StringAssert.Contains(admin.Access.PairUrl, access.Code);
            Assert.AreEqual(1, admin.Access.Devices.Count);
            Assert.AreEqual(AccessRole.Viewer, admin.Access.Devices[0].Role);

            // Viewer: Werte ja, Zugangscode nein – sonst wäre die Rolle wirkungslos
            var viewer = StateSnapshot.Parse(StateJson.WriteText(host.Engine, access, null, 8078, DateTimeOffset.Now, withHistory: true, withCode: false));
            Assert.AreEqual("", viewer.Access.Code, "ein Viewer darf den Code nicht lesen");
            Assert.AreEqual("", viewer.Access.PairUrl);
            Assert.IsTrue(viewer.Access.Remote, "der Schalter Home/VPN bleibt sichtbar");
            Assert.AreEqual(1, viewer.Access.Devices.Count, "die Geräteliste bleibt sichtbar");
            Assert.AreEqual(AccessRole.Viewer, viewer.Access.Devices[0].Role);
            StringAssert.Contains(StateJson.WriteText(host.Engine, access, null, 8078, DateTimeOffset.Now, withCode: false), "\"role\":\"viewer\"");
        }
        finally { try { Directory.Delete(paths.Root, true); } catch { } }
    }

    [TestMethod]
    public async Task Role_AViewerGetsNoActionDone_AnAdminDoes()
    {
        var paths = TempPaths("actions");
        Directory.CreateDirectory(paths.Root);
        try
        {
            var settings = AppSettings.Load(paths.SettingsFile);
            using var engine = new MonitorEngine(new FakePlatform(), paths, settings, readOnly: false);
            var access = new AccessControl(paths, new FakePlatform());
            var paired = access.Pair(access.Code, "Handy", "10.0.0.5")!.Value;
            var ctx = new ActionContext
            {
                Engine = engine,
                Launcher = new LaunchCoordinator(engine, new RemotePrompt()),
                Access = access,
                ServerPort = 8078,
            };
            var set = new ActionRequest { Action = "settings.set", Values = { ["intervalMs"] = "2000" } };

            // Admin: die Aktion läuft
            var asAdmin = await ActionApi.ExecuteAsync(set, ctx, new RemotePrompt(), role: AccessRole.Admin);
            Assert.IsTrue(asAdmin.Ok, asAdmin.Message);
            Assert.AreEqual(2000, settings.IntervalMs);

            // Dasselbe als Viewer: abgewiesen, und nichts wurde verändert
            var asViewer = await ActionApi.ExecuteAsync(
                new ActionRequest { Action = "settings.set", Values = { ["intervalMs"] = "5000" } }, ctx, new RemotePrompt(), role: AccessRole.Viewer);
            Assert.IsFalse(asViewer.Ok);
            StringAssert.Contains(asViewer.Message, Strings.ViewerOnly);
            Assert.AreEqual(2000, settings.IntervalMs, "die Einstellung ist unverändert");

            // Auch Start, Proxy, Geräteliste und der Server selbst bleiben einem Viewer verwehrt
            foreach (var action in new[] { "start", "stop", "proxy.set", "device.remove", "device.role", "code.rotate", "remote.set", "shutdown", "eval.start" })
            {
                var denied = await ActionApi.ExecuteAsync(new ActionRequest { Action = action, Arg = "x", Arg2 = AccessRole.Admin }, ctx, new RemotePrompt(), role: AccessRole.Viewer);
                Assert.IsFalse(denied.Ok, $"„{action}“ darf ein Viewer nicht ausführen");
                StringAssert.Contains(denied.Message, Strings.ViewerOnly, action);
            }
            Assert.IsFalse(ActionApi.ViewerAllowed("start"));
            Assert.IsFalse(ActionApi.ViewerAllowed("gibt-es-nicht"), "unbekannte Aktionen sind nie erlaubt");

            // Die Rolle selbst vergibt nur ein Admin – und die Meldung nennt die neuen Werte
            var promote = await ActionApi.ExecuteAsync(new ActionRequest { Action = "device.role", Arg = paired.Device.Id, Arg2 = AccessRole.Viewer }, ctx, new RemotePrompt(), role: AccessRole.Admin);
            Assert.IsTrue(promote.Ok, promote.Message);
            Assert.AreEqual(Strings.RoleViewer, promote.Message);
            Assert.AreEqual(AccessRole.Viewer, access.RoleOf(paired.Token, null));
            var bad = await ActionApi.ExecuteAsync(new ActionRequest { Action = "device.role", Arg = paired.Device.Id, Arg2 = "halfadmin" }, ctx, new RemotePrompt(), role: AccessRole.Admin);
            Assert.IsFalse(bad.Ok);
            StringAssert.Contains(bad.Message, "halfadmin");
        }
        finally { try { Directory.Delete(paths.Root, true); } catch { } }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slm-role-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}