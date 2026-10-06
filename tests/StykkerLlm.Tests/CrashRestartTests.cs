using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Automatischer Neustart nach einem Absturz. Die Regel ist bewusst im Core und rein, damit sie
// hier ohne Fenster, Server oder Simulator geprüft werden kann – nur das Starten selbst braucht den Server.
[TestClass]
public class CrashRestartTests
{
    private static Profile Profil(bool onCrash = true, int max = 0) => new()
    {
        Name = "mein Server", Program = @"C:\llama\llama-server.exe",
        Args = { "-m", "C:\\models\\a.gguf", "--port", "8090" },
        RestartOnCrash = onCrash, MaxRestarts = max,
    };

    [TestMethod]
    public void StandardIstAus_NeustartNurWennDasProfilEsWill()
    {
        var policy = new CrashRestartPolicy();
        var now = DateTimeOffset.Now;

        Assert.IsFalse(policy.Allows("k", Profil(onCrash: false), now), "Vorgabe: kein Neustart");
        Assert.IsFalse(policy.Allows("k", null, now), "ohne Profil gibt es nichts zu starten");
        Assert.IsFalse(policy.Allows("", Profil(), now), "ohne Schlüssel (Server von Hand gestartet) nicht");
        Assert.IsTrue(policy.Allows("k", Profil(), now), "mit Schalter wird neu gestartet");
    }

    [TestMethod]
    public void GrenzeIstErreicht_DannWirdNichtMehrGestartet()
    {
        var policy = new CrashRestartPolicy { MaxRestarts = 3, WindowMinutes = 10 };
        var p = Profil();
        var t0 = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(policy.Allows("k", p, t0), "1.");
        Assert.IsTrue(policy.Allows("k", p, t0.AddMinutes(1)), "2.");
        Assert.IsTrue(policy.Allows("k", p, t0.AddMinutes(2)), "3.");
        Assert.IsFalse(policy.Allows("k", p, t0.AddMinutes(3)), "danach nicht mehr");
        Assert.AreEqual((3, 3), policy.Count("k", t0.AddMinutes(3)));

        // Ein anderes Profil hat sein eigenes Konto
        Assert.IsTrue(policy.Allows("anderes", p, t0.AddMinutes(3)));
    }

    [TestMethod]
    public void NachDemFensterWirdNeuGezaehlt_UndResetSetztZurueck()
    {
        var policy = new CrashRestartPolicy { MaxRestarts = 2, WindowMinutes = 10 };
        var p = Profil();
        var t0 = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(policy.Allows("k", p, t0));
        Assert.IsTrue(policy.Allows("k", p, t0.AddMinutes(1)));
        Assert.IsFalse(policy.Allows("k", p, t0.AddMinutes(2)));

        Assert.IsTrue(policy.Allows("k", p, t0.AddMinutes(11)), "nach 10 min wieder erlaubt");
        Assert.AreEqual((1, 2), policy.Count("k", t0.AddMinutes(12)));

        policy.Reset("k");
        Assert.AreEqual((0, 2), policy.Count("k", t0.AddMinutes(13)));
    }

    [TestMethod]
    public void GrenzenVomProfilUndVonDerPolicy_WerdenBegrenzt()
    {
        var policy = new CrashRestartPolicy { MaxRestarts = 99, WindowMinutes = 999, DelaySeconds = 999 };
        Assert.AreEqual(10, policy.MaxRestartsClamped);
        Assert.AreEqual(120, policy.WindowMinutesClamped);
        Assert.AreEqual(120, policy.DelaySecondsClamped);

        var t0 = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        // Am Profil steht 2 → die Grenze gilt
        var p = Profil(max: 2);
        Assert.IsTrue(policy.Allows("k", p, t0));
        Assert.IsTrue(policy.Allows("k", p, t0.AddSeconds(1)));
        Assert.IsFalse(policy.Allows("k", p, t0.AddSeconds(2)));
        Assert.AreEqual(2, policy.MaxRestartsClamped);

        // MaxRestarts = 0 am Profil heißt „Vorgabe“, 0 in der Policy heißt „gar nicht“
        var aus = new CrashRestartPolicy { MaxRestarts = 0 };
        Assert.IsFalse(aus.Allows("k", Profil(max: 0), t0));
    }

    // Das Feld muss durch die Bibliothek und durch den Zustand des Servers – sonst wäre die Einstellung nach
    // einem Neustart weg und das Fenster zeigte sie nicht an.
    [TestMethod]
    public void EinstellungBleibtInDerBibliothekUndImZustand()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slm-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var paths = new AppPaths(dir);
            var lib = new Library(paths.LibraryFile);
            // Über den Verlaufseintrag, weil AddProfile sonst einen laufenden Server erwartet. Die Felder des Profils
            // kommen nicht aus dem Verlauf, deshalb hier setzen – genau das macht auch die Aktion profile.restart.
            var p = Profil();
            var angelegt = lib.AddProfile(new HistoryEntry { Key = p.Key, Program = p.Program, Args = p.Args, WorkingDir = p.WorkingDir }, p.Name, DateTime.Now);
            Assert.IsNotNull(angelegt);
            angelegt!.RestartOnCrash = true;
            lib.Save();
            Assert.IsTrue(File.Exists(paths.LibraryFile), "die Bibliothek wurde geschrieben: " + paths.LibraryFile);
            var roh = File.ReadAllText(paths.LibraryFile);
            StringAssert.Contains(roh, "\"RestartOnCrash\": true", "die Einstellung steht mit true in der Datei");

            // Zurücklesen mit Library.Load – der Konstruktor nimmt nur den Pfad zum Speichern an
            var again = Library.Load(paths.LibraryFile);
            var geladen = again.Profiles.Single();
            Assert.IsTrue(geladen.RestartOnCrash);
            Assert.AreEqual(0, geladen.MaxRestarts);

            // Zustand des Servers: additiv, Schema 1 – die beiden Felder müssen im JSON stehen. Der Simulator benutzt
            // denselben Datenordner und damit die gerade gespeicherte Bibliothek.
            using (var host = new SimHost(new List<SimServerSpec>(), dataDir: dir, seed: 2, autoStep: false))
            {
                var json = StateJson.WriteText(host.Engine, null, null, 8078, DateTimeOffset.Now);
                StringAssert.Contains(json, "\"restartOnCrash\":true");
                StringAssert.Contains(json, "\"maxRestarts\":0");
                var state = StateSnapshot.Parse(json);
                Assert.IsTrue(state.Profiles.Single().RestartOnCrash, "auch über den Zustand lesbar");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Der Absturz muss überhaupt einem Profil zugeordnet werden können: dazu trägt ServerLost den Schlüssel, unter dem
    // die **Profile** den Server führen. Vorher stand dort der Server-Schlüssel (Host:Port) – damit konnte der
    // Vergleich nie treffen und ein Neustart wäre nie ausgelöst worden (beim Live-Test aufgefallen).
    [TestMethod]
    public void VerlorenerServerTraegtDenProfilSchluessel()
    {
        var p = Profil();
        var lost = new ServerLost("mein Server", "http://127.0.0.1:8090", 4711, "C:\\logs\\a.log",
            new[] { "main: exiting" }, "Out of memory", DateTime.Now, p.Key);
        Assert.AreEqual(p.Key, lost.Key);
        Assert.AreEqual(p.Key, Library.MakeKey(p.Program, p.Args), "der Schlüssel ist derselbe wie bei den Profilen");

        // Und der Watcher liefert genau diesen Schlüssel – nicht seinen Host:Port-Schlüssel
        var info = new ServerInfo
        {
            Key = NetAddr.Key("127.0.0.1", 8090), Host = "127.0.0.1", Port = 8090,
            Program = p.Program, Args = p.Args.ToList(),
        };
        using var w = new ServerWatcher(info, new HttpClient(new FakeHandler()), new FakePlatform(),
            () => NetSnapshot.Empty, new ClientNamer(new FakePlatform()));
        Assert.AreEqual(p.Key, w.ProfileKey, "der Watcher führt denselben Schlüssel wie die Profile");
        Assert.AreNotEqual(w.Key, w.ProfileKey, "Key ist Host:Port – dafür findet man kein Profil");
    }
}