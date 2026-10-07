using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class LibraryTests
{
    private static ServerInfo Server(int pid = 10, long start = 1, string port = "8081", string model = "m.gguf", bool readable = true, string? extra = null)
    {
        var args = new List<string> { "-m", model, "--port", port, "-c", "4096" };
        if (extra != null) args.Add(extra);
        return new ServerInfo
        {
            Key = "127.0.0.1:" + port, Port = int.Parse(port), Pid = pid, StartTicks = start, Program = "C:\\x\\llama-server.exe",
            Args = args, CommandLineReadable = readable, Params = LlamaServerArgs.Parse(args), WorkingDir = "C:\\w",
        };
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    // U8: Tokens des letzten Laufs und wie er endete; ein neuer Lauf setzt das Ende zurück
    [TestMethod]
    public void LastRun_KeepsTokensAndEnd_NewRunClearsTheEnd()
    {
        var lib = new Library();
        var s = Server();
        lib.Observe(s, new Observation(40, null, null, Generated: 1200), T0);
        lib.Observe(s, new Observation(40, null, null, Generated: 5300), T0.AddSeconds(1));
        lib.RecordEnd(s, "crashed", T0.AddSeconds(2));
        var h = lib.History.Single();
        Assert.AreEqual(5300, h.LastRunTokens);
        Assert.AreEqual("crashed", h.LastEnd);
        Assert.AreEqual(T0.AddSeconds(2), h.LastEndAt);

        lib.Observe(Server(pid: 11, start: 2), new Observation(0, null, null), T0.AddMinutes(1));
        Assert.AreEqual(2, h.Runs);
        Assert.AreEqual("", h.LastEnd, "läuft wieder: kein Ende");
        lib.RecordEnd(Server(pid: 99, model: "other.gguf"), "clean", T0);   // unbekannter Server: nichts passiert
        Assert.AreEqual(1, lib.History.Count);
    }

    [TestMethod]
    public void ObserveBuildsStatisticsPerRun()
    {
        var lib = new Library();
        var s = Server();
        lib.Observe(s, new Observation(0, null, null), T0);
        lib.Observe(s, new Observation(40, 5.0, 3.5), T0.AddSeconds(1));
        lib.Observe(s, new Observation(60, 5.5, 3.5), T0.AddSeconds(2));
        lib.Observe(s, new Observation(0, 5.2, null), T0.AddSeconds(3));
        var h = lib.History.Single();
        Assert.AreEqual(1, h.Runs);
        Assert.AreEqual(3.0, h.TotalSeconds, 1e-9);
        Assert.AreEqual(60, h.BestTps);
        Assert.AreEqual(50, h.MeanTps, 1e-9);
        Assert.AreEqual(5.5, h.MaxVramGb);
        Assert.AreEqual(3.5, h.ModelSizeGb);
        Assert.AreEqual("m", h.Name);
        Assert.AreEqual(4096, h.Ctx);
        Assert.AreEqual(8081, h.Port);
        Assert.AreEqual(T0, h.FirstSeen);
        Assert.AreEqual(T0.AddSeconds(3), h.LastSeen);
        Assert.IsTrue(lib.Dirty);
    }

    [TestMethod]
    public void NewProcessCountsAsNewRunAndBigGapIsNotRuntime()
    {
        var lib = new Library();
        lib.Observe(Server(10, 1), new Observation(0, null, null), T0);
        lib.Observe(Server(10, 1), new Observation(0, null, null), T0.AddMinutes(10));   // Lücke: zählt nicht
        lib.Observe(Server(11, 2), new Observation(0, null, null), T0.AddMinutes(11));   // neuer Prozess, gleiche Kombination
        var h = lib.History.Single();
        Assert.AreEqual(2, h.Runs);
        Assert.AreEqual(0, h.TotalSeconds);
    }

    [TestMethod]
    public void DifferentArgumentsAreDifferentEntries()
    {
        var lib = new Library();
        lib.Observe(Server(extra: "--jinja"), new Observation(0, null, null), T0);
        lib.Observe(Server(), new Observation(0, null, null), T0);
        Assert.AreEqual(2, lib.History.Count);
    }

    [TestMethod]
    public void UnreadableCommandLineIsNotRecorded()
    {
        var lib = new Library();
        Assert.IsNull(lib.Observe(Server(readable: false), new Observation(1, null, null), T0));
        Assert.AreEqual(0, lib.History.Count);
    }

    [TestMethod]
    public void ProfilesAreDeduplicatedAndEditable()
    {
        var lib = new Library();
        var p = lib.AddProfile(Server(), null, T0)!;
        Assert.AreEqual("m", p.Name);
        Assert.AreSame(p, lib.AddProfile(Server(), "other", T0));
        Assert.AreEqual(1, lib.Profiles.Count);
        Assert.IsTrue(lib.RenameProfile(p.Id, "  Fast  "));
        Assert.IsFalse(lib.RenameProfile(p.Id, " "));
        Assert.IsTrue(lib.SetNote(p.Id, "note"));
        Assert.IsTrue(lib.UpdateArgs(p.Id, new[] { "-m", "n.gguf", "--port", "9", "--api-key", "SECRET" }));
        var e = lib.Profiles.Single();
        Assert.AreEqual("Fast", e.Name);
        Assert.AreEqual("note", e.Note);
        Assert.AreEqual(9, e.Port);
        Assert.AreEqual("n.gguf", e.ModelPath);
        Assert.IsTrue(e.HasSecrets);
        CollectionAssert.DoesNotContain(e.Args, "SECRET");
        Assert.IsNull(lib.FindProfile(Server(model: "m.gguf")));   // Argumente geändert: alter Schlüssel passt nicht mehr
        Assert.IsTrue(lib.RemoveProfile(p.Id));
        Assert.AreEqual(0, lib.Profiles.Count);
    }

    [TestMethod]
    public void ProfileFromUnsavableServerIsRefused()
    {
        var lib = new Library();
        Assert.IsNull(lib.AddProfile(Server(readable: false), null, T0));
        var router = new ServerInfo { Key = "k", Port = 1, Program = "p", CommandLineReadable = true, Mode = ServerMode.Router };
        Assert.IsNull(lib.AddProfile(router, null, T0));
    }

    [TestMethod]
    public void SaveAndLoadRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-lib-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "library.json");
        try
        {
            var lib = new Library(path);
            lib.AddProfile(Server(), "P", T0);
            lib.Observe(Server(), new Observation(33, 4.4, 3.5), T0);
            lib.Save();
            Assert.IsFalse(lib.Dirty);
            var back = Library.Load(path);
            Assert.AreEqual("P", back.Profiles.Single().Name);
            Assert.AreEqual(33, back.History.Single().BestTps);
            CollectionAssert.AreEqual(Server().Args.ToList(), back.History.Single().Args);
            Assert.IsNull(back.RecoveredFrom);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void CorruptFileIsMovedAside()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var lib = Library.Load(path);
            Assert.AreEqual(0, lib.Profiles.Count);
            Assert.IsNotNull(lib.RecoveredFrom);
            Assert.IsTrue(File.Exists(lib.RecoveredFrom));
            Assert.IsFalse(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void KeyIsStableAndCaseInsensitiveForProgram()
    {
        Assert.AreEqual(Library.MakeKey("C:\\A\\x.exe", new[] { "-m", "a" }), Library.MakeKey("c:\\a\\X.EXE", new[] { "-m", "a" }));
        Assert.AreNotEqual(Library.MakeKey("x", new[] { "-m", "a" }), Library.MakeKey("x", new[] { "-m", "b" }));
    }

    [TestMethod]
    public void KeyIgnoresLogFileAddedByLauncher()
    {
        var plain = Library.MakeKey("x", new[] { "-m", "a", "--port", "8090" });
        Assert.AreEqual(plain, Library.MakeKey("x", new[] { "-m", "a", "--port", "8090", "--log-file", @"C:\logs\a.log" }));
        Assert.AreEqual(plain, Library.MakeKey("x", new[] { "-m", "a", "--log-file=C:\\logs\\a.log", "--port", "8090" }));
    }

    [TestMethod]
    public void HistoryIsCappedButProfilesStay()
    {
        var lib = new Library();
        lib.AddProfile(Server(port: "1"), null, T0);
        for (int i = 0; i < Library.MaxHistory + 20; i++)
            lib.Observe(Server(pid: 100 + i, start: i, port: (1000 + i).ToString()), new Observation(0, null, null), T0.AddSeconds(i));
        lib.Observe(Server(port: "1"), new Observation(0, null, null), T0);   // das gemerkte Profil bleibt im Verlauf, auch wenn alt
        Assert.IsTrue(lib.History.Count <= Library.MaxHistory + 1);
    }

    [TestMethod]
    public void Library_LockedFileEntersReadOnlyModeAndRecovers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-lib-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");
        try
        {
            var lib = new Library(path);
            lib.AddProfile(Server(), "P", T0);
            lib.Save();

            // Datei exklusiv sperren
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var loaded = Library.Load(path, ioRetries: 1, retryDelayMs: 10);
                Assert.IsTrue(loaded.ReadOnly);
                Assert.IsNotNull(loaded.ReadOnlyReason);
                Assert.AreEqual(0, loaded.Profiles.Count);

                // Solange gesperrt: TryRecover schlägt fehl
                Assert.IsFalse(loaded.TryRecover());
                Assert.IsTrue(loaded.ReadOnly);
            }

            // Nach Freigabe: TryRecover lädt die Bibliothek erfolgreich
            var recovered = Library.Load(path, ioRetries: 0, retryDelayMs: 0);
            Assert.IsFalse(recovered.ReadOnly);
            Assert.AreEqual("P", recovered.Profiles.Single().Name);
        }
        finally { Directory.Delete(dir, true); }
    }
}

[TestClass]
public class LibraryRunCountTests
{
    [TestMethod]
    public void MonitorRestartDoesNotCountAsNewRun_ButNewProcessDoes()
    {
        var path = Path.Combine(Path.GetTempPath(), "slm-runs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ServerInfo S(int pid, long start) => new()
            {
                Key = "k", Port = 8081, Pid = pid, StartTicks = start, Program = "p.exe", CommandLineReadable = true,
                Args = new[] { "-m", "a.gguf" }, Params = LlamaServerArgs.Parse(new[] { "-m", "a.gguf" }),
            };
            var t = new DateTime(2026, 1, 1, 12, 0, 0);
            var lib = new Library(path);
            lib.Observe(S(10, 500), new Observation(0, null, null), t);
            lib.Save();
            // Monitor neu gestartet, derselbe Serverprozess läuft weiter
            var lib2 = Library.Load(path);
            lib2.Observe(S(10, 500), new Observation(0, null, null), t.AddMinutes(30));
            Assert.AreEqual(1, lib2.History.Single().Runs);
            // der Server wurde neu gestartet
            lib2.Observe(S(11, 900), new Observation(0, null, null), t.AddMinutes(31));
            Assert.AreEqual(2, lib2.History.Single().Runs);
        }
        finally { File.Delete(path); }
    }
}
