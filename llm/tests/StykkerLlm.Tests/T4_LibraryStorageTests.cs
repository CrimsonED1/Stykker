using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class T4_LibraryStorageTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "slm-t4-" + Guid.NewGuid().ToString("N"));

    private static ServerInfo Server(string port = "8081", string model = "a.gguf") => new()
    {
        Key = "127.0.0.1:" + port, Port = int.Parse(port), Pid = 10, StartTicks = 1, Program = "p.exe",
        Args = new[] { "-m", model, "--port", port, "-c", "4096" }, CommandLineReadable = true,
        Params = LlamaServerArgs.Parse(new[] { "-m", model, "--port", port, "-c", "4096" }), WorkingDir = "C:\\w",
    };

    [TestMethod]
    public void LoadToleratesUnknownJsonFields()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");
        try
        {
            File.WriteAllText(path,
                "{\"Version\":1,\"Future\":true,\"Profiles\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Name\":\"P\",\"Program\":\"p.exe\"," +
                "\"Args\":[\"-m\",\"a.gguf\"],\"Xyzzy\":{\"a\":[1,2]}}],\"History\":[],\"Benchmarks\":[]}");
            var lib = Library.Load(path);
            Assert.AreEqual("P", lib.Profiles.Single().Name);
            Assert.AreEqual("p.exe", lib.Profiles.Single().Program);
            Assert.IsNull(lib.RecoveredFrom);

            lib.Save();   // Unbekannte Felder fallen beim erneuten Speichern heraus
            Assert.IsFalse(File.ReadAllText(path).Contains("Xyzzy"));
            var back = Library.Load(path);
            Assert.AreEqual("P", back.Profiles.Single().Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void EmptyWhitespaceAndNullFilesAreMovedAside()
    {
        foreach (var content in new[] { "", "   ", "null" })
        {
            var dir = TempDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "library.json");
            try
            {
                File.WriteAllText(path, content);
                var lib = Library.Load(path);
                Assert.IsNotNull(lib.RecoveredFrom, "'" + content + "'");
                Assert.IsTrue(File.Exists(lib.RecoveredFrom));
                Assert.IsFalse(File.Exists(path));
                Assert.AreEqual(0, lib.Profiles.Count);
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [TestMethod]
    public void PruneBadKeepsOnlyTheNewestFive()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "library.json");
            for (int i = 1; i <= 7; i++)
            {
                var f = path + ".bad-2026010" + i + "-000000";
                File.WriteAllText(f, "x");
                File.SetLastWriteTimeUtc(f, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i));
            }
            Library.PruneBad(path);
            var left = Directory.GetFiles(dir, "library.json.bad-*");
            Assert.AreEqual(Library.KeepBadFiles, left.Length);
            Assert.IsFalse(File.Exists(path + ".bad-20260101-000000"));
            Assert.IsFalse(File.Exists(path + ".bad-20260102-000000"));
            Assert.IsTrue(File.Exists(path + ".bad-20260103-000000"));
            Assert.IsTrue(File.Exists(path + ".bad-20260107-000000"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SavedProfileNeverContainsPlainSecrets()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");
        try
        {
            var lib = new Library(path);
            var p = lib.AddProfile(Server(), null, T0)!;
            lib.UpdateProfile(p.Id, "P", null, "p.exe", new[] { "-m", "a.gguf", "--port", "8081", "--api-key", "sk-LIVE-SECRET-42" }, null);
            lib.Save();
            var text = File.ReadAllText(path);
            Assert.IsFalse(text.Contains("sk-LIVE-SECRET-42"));
            Assert.IsTrue(text.Contains("***"));

            var back = Library.Load(path);
            var stored = back.Profiles.Single();
            Assert.IsTrue(stored.HasSecrets);
            CollectionAssert.AreEqual(new[] { "-m", "a.gguf", "--port", "8081", "--api-key", "***" }, stored.Args);
            Assert.AreEqual(8081, stored.Port);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ConfirmFingerprintSurvivesSaveAndLoad()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");
        try
        {
            var lib = new Library(path);
            var p = lib.AddProfile(Server(), null, T0)!;
            lib.Confirm(p.Id, "", "fp-1");
            Assert.IsTrue(lib.IsConfirmed(p.Id, "", "fp-1"));
            Assert.IsFalse(lib.IsConfirmed(p.Id, "", "fp-2"));
            lib.Save();

            var back = Library.Load(path);
            Assert.IsTrue(back.IsConfirmed(p.Id, "", "fp-1"));
            Assert.IsFalse(back.IsConfirmed(p.Id, "", "fp-2"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void HistoryKeepsEnvSecretMarkersThroughRoundTrip()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");
        try
        {
            var lib = new Library(path);
            var info = new ServerInfo
            {
                Key = "k", Port = 8081, Pid = 10, StartTicks = 1, Program = "p.exe", CommandLineReadable = true,
                Args = new[] { "-m", "a.gguf" }, Params = LlamaServerArgs.Parse(new[] { "-m", "a.gguf" }),
                HasSecrets = true, Env = new Dictionary<string, string> { ["LLAMA_ARG_API_KEY"] = "***" },
            };
            Assert.IsNotNull(lib.Observe(info, new Observation(10, null, null), T0));
            lib.Save();

            var back = Library.Load(path);
            var h = back.History.Single();
            Assert.IsTrue(h.HasSecrets);
            Assert.AreEqual("***", h.Env["LLAMA_ARG_API_KEY"]);
            Assert.IsFalse(File.ReadAllText(path).Contains("plain"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void LibraryWithoutPathStaysInMemory()
    {
        var lib = new Library();
        Assert.IsNull(lib.FilePath);
        lib.AddProfile(Server(), null, T0);
        Assert.IsTrue(lib.Dirty);
        lib.Save();   // kein Pfad: kein Fehler, kein Schreiben
        Assert.IsTrue(lib.Dirty);
        Assert.IsFalse(lib.TryRecover());
        Assert.AreEqual(1, lib.Profiles.Count);
    }

    [TestMethod]
    public void MaxHistoryCappingKeepsNewestEntries()
    {
        var lib = new Library();
        for (int i = 0; i < Library.MaxHistory + 5; i++)
        {
            var info = new ServerInfo
            {
                Key = "k" + i, Port = 1000 + i, Pid = 10, StartTicks = 1, Program = "p" + i + ".exe", CommandLineReadable = true,
                Args = new[] { "-m", "m" + i + ".gguf" }, Params = LlamaServerArgs.Parse(new[] { "-m", "m" + i + ".gguf" }),
            };
            lib.Observe(info, new Observation(0, null, null), T0.AddMinutes(i));
        }
        Assert.AreEqual(Library.MaxHistory, lib.History.Count);
        Assert.IsNull(lib.FindHistory(Library.MakeKey("p0.exe", new[] { "-m", "m0.gguf" })));   // ältester Eintrag ist weg
        Assert.IsNotNull(lib.FindHistory(Library.MakeKey("p" + (Library.MaxHistory + 4) + ".exe", new[] { "-m", "m" + (Library.MaxHistory + 4) + ".gguf" })));
    }
}