using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N80 – Strata-Feinschliff: (1) Start-Karte ordnet den lauschenden Kindprozess dem Start zu, (2) „Gemerkt“ mit einem Vergleich
// für Profil, Verlauf und Start, (3) Cache-Zahl aus den Fortschrittszeilen des Strata-Protokolls, (4) Bildtest („Test vision models“).
// Ohne Netz: Test-Plattform, scripted Handler.

[TestClass]
public class N80_StartCardTests
{
    private static LaunchedServer Launch(int pid, int port)
    {
        var l = new LaunchedServer { Name = "strata", Port = port };
        l.Pid = pid;
        return l;
    }

    private static ServerInfo Listener(int pid, int port, int parentPid) =>
        new() { Key = "127.0.0.1:" + port, Host = "127.0.0.1", Port = port, Pid = pid, ParentPid = parentPid };

    private static void Proc(FakePlatform p, int pid, int parent) =>
        p.Processes[pid] = new ProcessDetails(pid, 1000 + pid, parent, null, null, null, new Dictionary<string, string>());

    [TestMethod]
    public void The_started_process_itself_counts_as_started()
    {
        var p = new FakePlatform();
        Assert.IsTrue(ServerRegistry.StartedBy(p, Launch(100, 8082), Listener(100, 8082, 1)));
    }

    [TestMethod]
    public void The_base_python_child_of_the_venv_starter_counts_as_started()
    {
        // .venv\Scripts\python.exe (100) startet das Basis-Python (200), das auf dem Port lauscht
        var p = new FakePlatform();
        Assert.IsTrue(ServerRegistry.StartedBy(p, Launch(100, 8082), Listener(200, 8082, 100)));
    }

    [TestMethod]
    public void A_descendant_up_to_three_levels_down_counts_as_started()
    {
        var p = new FakePlatform();
        Proc(p, 300, 200);
        Proc(p, 200, 100);
        // 400 (lauscht) <- 300 <- 200 <- 100 (Start): dritte Ebene
        Assert.IsTrue(ServerRegistry.StartedBy(p, Launch(100, 8082), Listener(400, 8082, 300)));
    }

    [TestMethod]
    public void A_fourth_level_is_too_far()
    {
        var p = new FakePlatform();
        Proc(p, 300, 200);
        Proc(p, 200, 150);
        Proc(p, 150, 100);
        Assert.IsFalse(ServerRegistry.StartedBy(p, Launch(100, 8082), Listener(400, 8082, 300)));
    }

    [TestMethod]
    public void A_foreign_server_on_the_same_port_does_not_count()
    {
        // Der Port allein reicht nicht: ein fremder Prozess (Elternkette ohne den Start) lauscht auf 8082
        var p = new FakePlatform();
        Proc(p, 555, 500);
        Proc(p, 500, 1);
        Assert.IsFalse(ServerRegistry.StartedBy(p, Launch(100, 8082), Listener(555, 8082, 500)));
    }

    [TestMethod]
    public void A_descendant_on_another_port_does_not_count()
    {
        var p = new FakePlatform();
        Proc(p, 200, 100);
        Assert.IsFalse(ServerRegistry.StartedBy(p, Launch(100, 8082), Listener(200, 8083, 100)));
    }
}

[TestClass]
public class N80_SavedFlagTests
{
    // Erfundene Pfade: der Basis-Python-Ordner muss nicht existieren, nur der venv-Starter im Arbeitsordner
    private const string BasePython = @"C:\apps\strata\python312\python.exe";
    private static readonly string[] Args = { "serve\\server.py", "--engine", "strata", "--port", "8082" };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n80-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WithVenv(string dir)
    {
        var scripts = Path.Combine(dir, ".venv", "Scripts");
        Directory.CreateDirectory(scripts);
        var py = Path.Combine(scripts, "python.exe");
        File.WriteAllText(py, "");
        return py;
    }

    [TestMethod]
    public void The_venv_starter_and_the_base_python_have_the_same_comparison_key()
    {
        var dir = TempDir();
        try
        {
            var venvPy = WithVenv(dir);
            Assert.AreEqual(Library.CompareKey(BasePython, Args, dir), Library.CompareKey(venvPy, Args, dir));
            // Ohne venv im Arbeitsordner bleibt das Basis-Python ein eigenes Programm
            var other = TempDir();
            try { Assert.AreNotEqual(Library.CompareKey(BasePython, Args, other), Library.CompareKey(venvPy, Args, other)); }
            finally { Directory.Delete(other, true); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void A_profile_saved_with_the_venv_starter_is_found_from_the_listening_base_python_and_not_saved_twice()
    {
        var dir = TempDir();
        try
        {
            var venvPy = WithVenv(dir);
            var lib = new Library();
            var now = new DateTime(2026, 10, 15, 12, 0, 0);
            // Der Monitor sieht das lauschende Basis-Python; gemerkt wurde der venv-Starter
            var listening = new ServerInfo
            {
                Key = "127.0.0.1:8082", Host = "127.0.0.1", Port = 8082, Pid = 200, StartTicks = 5, ParentPid = 100,
                Program = BasePython, Args = Args, WorkingDir = dir, CommandLineReadable = true,
            };
            lib.Observe(listening, new Observation(0, null, null), now);
            var venvEntry = new HistoryEntry { Program = venvPy, Args = Args.ToList(), WorkingDir = dir, Name = "strata", Port = 8082 };
            var profile = lib.AddProfile(venvEntry, "strata", now);
            Assert.IsNotNull(profile);

            Assert.AreSame(profile, lib.FindProfile(listening), "Der laufende Server gehört zum gemerkten Profil");
            var hist = lib.HistoryOf(profile!);
            Assert.IsNotNull(hist, "Der Verlaufseintrag gehört zum Profil");
            Assert.AreEqual(Library.CompareKey(hist!.Program, hist.Args, hist.WorkingDir), Library.CompareKey(profile!.Program, profile.Args, profile.WorkingDir));

            // Merken aus dem Verlauf legt kein zweites Profil an
            var again = lib.AddProfile(hist, null, now);
            Assert.AreSame(profile, again);
            Assert.AreEqual(1, lib.Profiles.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void A_server_with_other_arguments_is_not_the_saved_profile()
    {
        var dir = TempDir();
        try
        {
            var venvPy = WithVenv(dir);
            var lib = new Library();
            lib.AddProfile(new HistoryEntry { Program = venvPy, Args = Args.ToList(), WorkingDir = dir, Name = "strata" }, "strata", DateTime.Now);
            var other = new ServerInfo
            {
                Key = "127.0.0.1:8083", Host = "127.0.0.1", Port = 8083, Pid = 300, Program = BasePython,
                Args = new[] { "serve\\server.py", "--engine", "strata", "--port", "8083" }, WorkingDir = dir, CommandLineReadable = true,
            };
            Assert.IsNull(lib.FindProfile(other));
        }
        finally { Directory.Delete(dir, true); }
    }
}

[TestClass]
public class N80_StrataCacheTests
{
    // Die Zeilen aus dem Protokoll, wie Strata sie schreibt (Tausendertrennzeichen „,“)
    private static readonly string[] Lines =
    {
        "[strata] reading the prompt: 49 of 54 tokens, 3 s so far",
        "[strata] reading the prompt: 49,161 tokens, 10 s so far",
        "[strata] reading the prompt: 8,192 of 49,161 tokens, 15 s so far",
        "[strata] reading the prompt: 49,156 of 49,161 tokens, 31 s so far",
        "[strata] writing a tool call: mcp__terminal__run_in_terminal: 87 of max 32000 tokens, 36.3 tok/s, 34 s",
        "[strata] done: 109 tokens in 35 s (37.9 tok/s) (stop, cancel=False), expert cache 73.8% hit (+2.0% of the routed experts over PCIe)",
        "[strata] reading the prompt: 49,303 of 49,308 tokens, 1 s so far",
        "[strata] answering: 258 of max 32000 tokens, 30.8 tok/s, 8 s",
        "[strata] done: 263 tokens in 8 s (39.9 tok/s) (stop, cancel=False), expert cache 77.6% hit (+1.6% of the routed experts over PCIe)",
    };

    private static StrataApi.ProgressLog Feed(params string[] lines)
    {
        var log = new StrataApi.ProgressLog();
        foreach (var l in lines) log.Feed(l);
        return log;
    }

    [TestMethod]
    public void The_example_log_gives_one_request_per_prompt_with_the_estimated_reuse()
    {
        var log = Feed(Lines);
        var reqs = log.Requests;
        Assert.AreEqual(3, reqs.Count, "Drei Anfragen: die dritte beginnt nach dem ersten done, die erste ohne done bleibt offen");

        // Erste Anfrage: 49 von 54 in 3 s – die Zeit ist über 2 s, darum nichts wiederverwendet
        Assert.AreEqual(54, reqs[0].Total);
        Assert.AreEqual(0, reqs[0].Reused);

        // Zweite Anfrage: 49.161 Token, vollständig gelesen; letzte Lesezeile bei 31 s, Antwort 109 Token mit 37,9 t/s
        Assert.AreEqual(49161, reqs[1].Total);
        Assert.AreEqual(0, reqs[1].Reused);
        Assert.AreEqual(31, reqs[1].ReadSeconds, 0.001);
        Assert.IsTrue(reqs[1].Done);
        Assert.AreEqual(109, reqs[1].OutputTokens);
        Assert.AreEqual(37.9, reqs[1].Tps, 0.001);

        // Dritte Anfrage: 49.303 von 49.308 in 1 s – aus dem Cache, nur 5 Token neu gelesen
        Assert.AreEqual(49308, reqs[2].Total);
        Assert.AreEqual(49303, reqs[2].Reused);
        Assert.IsTrue(reqs[2].Done);
        Assert.AreEqual(263, reqs[2].OutputTokens);
        Assert.AreEqual(39.9, reqs[2].Tps, 0.001);
    }

    [TestMethod]
    public void Only_the_first_reading_line_decides_and_the_read_part_is_the_rest()
    {
        var log = Feed("[strata] reading the prompt: 49,303 of 49,308 tokens, 1 s so far",
            "[strata] reading the prompt: 49,308 of 49,308 tokens, 3 s so far");
        var r = log.Requests.Single();
        Assert.AreEqual(49303, r.Reused);
        Assert.AreEqual(3, r.ReadSeconds, 0.001, "Die Lesezeit ist die der letzten Lesezeile");
        var line = r.ToPromptLine();
        Assert.AreEqual(49308, line.Total);
        Assert.AreEqual(49303, line.Reused);
        Assert.AreEqual(5, line.Read);
        Assert.IsTrue(line.Estimated, "Strata nennt die Wiederverwendung nicht – das ist geschätzt");
    }

    [TestMethod]
    public void A_reused_part_needs_at_least_half_and_at_most_two_seconds()
    {
        int Reused(string first) => Feed("[strata] reading the prompt: " + first).Requests.Single().Reused;

        Assert.AreEqual(3000, Reused("3,000 of 5,000 tokens, 1 s so far"), "60 % in 1 s");
        Assert.AreEqual(0, Reused("2,000 of 5,000 tokens, 1 s so far"), "40 % sind zu wenig");
        Assert.AreEqual(0, Reused("4,000 of 5,000 tokens, 2.5 s so far"), "über 2 s zählt nicht");
        Assert.AreEqual(2500, Reused("2,500 of 5,000 tokens, 2 s so far"), "genau 50 % und genau 2 s gelten");
        Assert.AreEqual(0, Reused("5,000 tokens, 1 s so far"), "ohne X: ganz neu gelesen");
    }

    [TestMethod]
    public void A_new_prompt_size_without_done_starts_a_new_request_and_an_answer_ends_the_reading()
    {
        var log = new StrataApi.ProgressLog();
        Assert.IsNotNull(log.Feed("[strata] reading the prompt: 1,000 tokens, 1 s so far"), "erste Zeile eröffnet");
        Assert.IsNull(log.Feed("[strata] reading the prompt: 1,000 tokens, 2 s so far"), "gleicher Umfang: dieselbe Anfrage");
        Assert.IsNotNull(log.Feed("[strata] reading the prompt: 2,000 tokens, 1 s so far"), "anderer Umfang: neue Anfrage");
        log.Feed("[strata] answering: 5 of max 100 tokens, 9 tok/s, 1 s");
        Assert.IsNotNull(log.Feed("[strata] reading the prompt: 2,000 tokens, 1 s so far"), "nach der Antwort beginnt eine neue Anfrage");
        Assert.AreEqual(3, log.Requests.Count);
    }

    [TestMethod]
    public void The_old_serve_line_still_reads_as_before()
    {
        var p = StrataApi.ParsePromptLine("strata serve: prompt 8067 tokens = 100 reused + 7967 read in 4123 ms (x)");
        Assert.IsNotNull(p);
        Assert.AreEqual(8067, p!.Total);
        Assert.AreEqual(100, p.Reused);
        Assert.AreEqual(7967, p.Read);
        Assert.IsFalse(p.Estimated);
        Assert.IsNull(StrataApi.ParsePromptLine(Lines[0]), "Fortschrittszeilen sind kein serve-Format");
    }
}

[TestClass]
public class N80_VisionTestTests
{
    private const string Url = "https://cloud.example/v1";
    private const string Key = "sk-test-123";

    // Antwortet je Modell; merkt sich Modelle, Zugangsschlüssel und Anfragetexte
    private sealed class Scripted : HttpMessageHandler
    {
        public Func<string, (HttpStatusCode Code, string Body)> Respond { get; set; } = _ => (HttpStatusCode.OK, Chat("Red and blue."));
        public List<string> Models { get; } = new();
        public List<string?> Auth { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            lock (Models) { Models.Add(model); Auth.Add(request.Headers.Authorization?.ToString()); Bodies.Add(body); }
            var (code, text) = Respond(model);
            return new HttpResponseMessage(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static string Chat(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n80-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static VisionTest Make(Scripted h, IEnumerable<string> models, string dir, string? csv = null, string? dead = null, bool retry = false, bool withKey = true)
    {
        var keys = new ProviderKeys(dir, new FakePlatform { UserProtection = true });
        if (withKey) keys.Set(Url, Key);
        return new VisionTest(Url, models, keys, new HttpClient(h), csv, timeout: TimeSpan.FromSeconds(5),
            pause: TimeSpan.FromMilliseconds(1), retryWait: TimeSpan.FromMilliseconds(50), deadPath: dead, retryUnavailable: retry);
    }

    private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    [TestMethod]
    public void The_built_in_image_is_a_64_pixel_png_with_red_left_and_blue_right()
    {
        var png = VisionTest.TestImage();
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
        Assert.AreEqual(64, BigEndian(png, 16), "Breite");
        Assert.AreEqual(64, BigEndian(png, 20), "Höhe");

        // Die Bilddaten (IDAT) entpacken: je Zeile ein Filterbyte und 64 Pixel zu drei Farbkanälen
        int idat = IndexOf(png, "IDAT");
        int len = BigEndian(png, idat - 4);
        using var z = new ZLibStream(new MemoryStream(png, idat + 4, len), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var pixels = raw.ToArray();
        Assert.AreEqual(64 * (1 + 64 * 3), pixels.Length);
        // Pixel 31 (links, Rot), Pixel 32 (rechts, Blau) in der ersten Zeile
        Assert.AreEqual(255, pixels[1 + 31 * 3], "rot");
        Assert.AreEqual(0, pixels[1 + 31 * 3 + 2]);
        Assert.AreEqual(0, pixels[1 + 32 * 3], "kein Rot rechts");
        Assert.AreEqual(255, pixels[1 + 32 * 3 + 2], "blau");
    }

    private static int IndexOf(byte[] data, string text)
    {
        var pat = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i + pat.Length <= data.Length; i++)
            if (data.AsSpan(i, pat.Length).SequenceEqual(pat)) return i;
        return -1;
    }

    [TestMethod]
    public void The_answer_must_name_both_colours()
    {
        Assert.IsTrue(VisionTest.Describes("Red and blue."));
        Assert.IsTrue(VisionTest.Describes("BLUE, then red"));
        Assert.IsFalse(VisionTest.Describes("red"), "eine Farbe reicht nicht");
        Assert.IsFalse(VisionTest.Describes("reddish blue"), "ganze Wörter zählen");
        Assert.IsFalse(VisionTest.Describes("green and yellow"));
    }

    [TestMethod]
    public async Task A_model_that_names_both_colours_is_ok_and_the_request_carries_the_image()
    {
        var h = new Scripted();
        var dir = TempDir();
        var csv = Path.Combine(dir, "vision-test.csv");
        try
        {
            var run = Make(h, new[] { "vision-a" }, dir, csv);
            await run.RunAsync();
            var r = run.Results.Single();
            Assert.AreEqual("200", r.Status);
            Assert.IsTrue(r.Ok);
            Assert.AreEqual("", r.Note);
            Assert.IsNotNull(r.TotalSec);
            Assert.AreEqual("Bearer " + Key, h.Auth.Single());
            Assert.IsTrue(h.Bodies.Single().Contains("data:image/png;base64,"), "das Bild geht als Datenadresse mit");
            Assert.IsTrue(h.Bodies.Single().Contains(VisionTest.Question));
            var lines = File.ReadAllLines(csv);
            Assert.AreEqual(VisionTest.CsvHeader, lines[0]);
            Assert.AreEqual(2, lines.Length);
            Assert.IsTrue(lines[1].EndsWith(",yes,"), lines[1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task A_wrong_answer_is_not_ok_and_says_why_and_a_non_200_is_not_ok()
    {
        var h = new Scripted { Respond = m => m == "wrong" ? (HttpStatusCode.OK, Chat("Green.")) : (HttpStatusCode.OK, Chat("red and blue")) };
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "wrong" }, dir);
            await run.RunAsync();
            var r = run.Results.Single();
            Assert.IsFalse(r.Ok);
            StringAssert.Contains(r.Note, "does not name both colours");
        }
        finally { Directory.Delete(dir, true); }

        var h2 = new Scripted { Respond = _ => (HttpStatusCode.Accepted, Chat("red and blue")) };
        var dir2 = TempDir();
        try
        {
            var run = Make(h2, new[] { "m" }, dir2);
            await run.RunAsync();
            var r = run.Results.Single();
            Assert.AreEqual("202", r.Status);
            Assert.IsFalse(r.Ok, "nur 200 gilt als Ok");
        }
        finally { Directory.Delete(dir2, true); }
    }

    [TestMethod]
    public async Task A_model_the_provider_does_not_know_is_remembered_and_skipped_next_time()
    {
        var h = new Scripted { Respond = _ => (HttpStatusCode.NotFound, "{\"error\":{\"message\":\"model not found\"}}") };
        var dir = TempDir();
        var dead = Path.Combine(dir, "classifier-dead.txt");
        try
        {
            var first = Make(h, new[] { "gone" }, dir, dead: dead);
            await first.RunAsync();
            Assert.AreEqual("404", first.Results.Single().Status);
            Assert.AreEqual(1, h.Models.Count);
            Assert.IsTrue(File.ReadAllText(dead).Contains(Url + "|gone"));

            var second = Make(h, new[] { "gone" }, dir, dead: dead);
            await second.RunAsync();
            var r = second.Results.Single();
            Assert.AreEqual(Strings.ClassifierStatusSkipped, r.Status);
            Assert.AreEqual(1, h.Models.Count, "Das tote Modell wird nicht noch einmal gefragt");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Models_with_a_skipped_word_are_not_sent_and_missing_keys_send_nothing()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "text-embedding-3", "chat-model" }, dir);
            await run.RunAsync();
            Assert.AreEqual(2, run.Results.Count);
            Assert.AreEqual(Strings.ClassifierStatusSkipped, run.Results.First(r => r.Model == "text-embedding-3").Status);
            CollectionAssert.AreEqual(new[] { "chat-model" }, h.Models.ToArray());
        }
        finally { Directory.Delete(dir, true); }

        var h2 = new Scripted();
        var dir2 = TempDir();
        try
        {
            var run = Make(h2, new[] { "chat-model" }, dir2, withKey: false);
            await run.RunAsync();
            Assert.AreEqual(0, h2.Models.Count, "ohne Schlüssel geht nichts ins Netz");
            Assert.AreEqual(Strings.ProxyProviderKeyMissingShort, run.Error);
        }
        finally { Directory.Delete(dir2, true); }
    }

    [TestMethod]
    public void The_table_puts_ok_fastest_first_and_skipped_last()
    {
        var rows = new[]
        {
            new VisionResult(DateTime.Now, "skipped-one", Strings.ClassifierStatusSkipped, null, false, ""),
            new VisionResult(DateTime.Now, "slow-ok", "200", 9.0, true, ""),
            new VisionResult(DateTime.Now, "fast-ok", "200", 1.2, true, ""),
            new VisionResult(DateTime.Now, "wrong", "200", 0.5, false, "x"),
        };
        CollectionAssert.AreEqual(new[] { "fast-ok", "slow-ok", "wrong", "skipped-one" }, VisionTest.Sorted(rows).Select(r => r.Model).ToArray());
    }
}
