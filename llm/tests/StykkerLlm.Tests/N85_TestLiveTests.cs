using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N85 – Live-Anzeige der Modelltests („Test classifier models“ und „Test vision models“ im Proxy-Dialog): Phasen der laufenden
// Anfrage, Zähler, grobe Restzeit, die übersetzten Notizen. Ohne Netz: scripted Handler und eine Uhr, die der Test stellt.
[TestClass]
public class N85_TestLiveTests
{
    private const string Url = "https://cloud.example/v1";
    private const string Key = "sk-test-456";

    // Antwortet je Modell; merkt sich die Modelle, die ankamen
    private sealed class Scripted : HttpMessageHandler
    {
        public Func<string, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Chat("<block>no</block>"));
        public List<string> Models { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            lock (Models) Models.Add(model);
            return await Respond(model, ct);
        }
    }

    private static HttpResponseMessage Chat(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }),
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Error(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n85-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProviderKeys Keys(string dir)
    {
        var keys = new ProviderKeys(dir, new FakePlatform { UserProtection = true });
        keys.Set(Url, Key);
        return keys;
    }

    private static ClassifierTest Classifier(Scripted h, IEnumerable<string> models, string dir, DateTime? clock = null,
        TimeSpan? timeout = null, TimeSpan? retryWait = null, string? deadPath = null)
    {
        var t = clock ?? new DateTime(2026, 10, 15, 12, 0, 0);
        return new ClassifierTest(Url, models, new ClassifierRequest(ClassifierTest.SampleBody(), false), Keys(dir), new HttpClient(h),
            null, now: () => t, timeout: timeout ?? TimeSpan.FromSeconds(5), pause: TimeSpan.FromMilliseconds(1),
            retryWait: retryWait ?? TimeSpan.FromMilliseconds(50), deadPath: deadPath);
    }

    // ── Phasen und Zähler ──

    [TestMethod]
    public async Task A_request_is_waiting_for_the_first_byte_while_it_runs_and_idle_afterwards()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            var seen = new List<TestPhase>();
            var run = Classifier(h, new[] { "fast-model" }, dir);
            h.Respond = (_, _) => { seen.Add(run.Status.Phase); return Task.FromResult(Chat("<block>no</block>")); };
            await run.RunAsync();
            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual(TestPhase.WaitingFirstByte, seen[0], "vor der Antwort steht die Phase „waiting for the first byte“");
            Assert.AreEqual(TestPhase.Idle, run.Status.Phase, "nach dem Lauf läuft nichts mehr");
            Assert.AreEqual("", run.Status.Model);
            Assert.AreEqual(0.0, run.Status.RequestSeconds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task B_counts_split_into_ok_failed_and_skipped()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            // fast: Format ok · gone: 404 (also „nicht verfügbar“) · plain: Format falsch · text-embedding: übersprungen (Einbettung)
            h.Respond = (model, _) => Task.FromResult(model switch
            {
                "fast" => Chat("<block>no</block>"),
                "gone" => Error(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Function x Not found for account\"}}"),
                _ => Chat("hello"),
            });
            var run = Classifier(h, new[] { "fast", "gone", "plain", "text-embedding" }, dir);
            await run.RunAsync();
            Assert.AreEqual(3, run.Results.Count(r => r.Status != Strings.ClassifierStatusSkipped), "drei Modelle sind gefragt");
            Assert.AreEqual((1, 2, 1), run.Counts);
            Assert.AreEqual(4, run.Done);
            Assert.AreEqual(4, run.Total);
            Assert.AreEqual(3, h.Models.Count, "das übersprungene Modell geht nicht ins Netz");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task C_a_rate_limit_shows_the_wait_phase_for_the_retry_time()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            // Erste Antwort 429, danach nach der Wartezeit eine gute Antwort. Die Phase „waiting … (rate limit)“ muss zu sehen sein.
            int calls = 0;
            h.Respond = (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? Error((HttpStatusCode)429, "{\"error\":\"slow down\"}")
                : Chat("<block>no</block>"));
            var run = Classifier(h, new[] { "limited" }, dir, retryWait: TimeSpan.FromMilliseconds(400));
            var sawWait = false;
            var task = run.RunAsync();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                if (run.Status.Phase == TestPhase.RateLimitWait)
                {
                    sawWait = true;
                    Assert.AreEqual(0.4, run.Status.WaitSeconds, 1e-9);
                    Assert.AreEqual(0.0, run.Status.RequestSeconds, "während der Wartezeit läuft keine Anfrage");
                    break;
                }
                await Task.Delay(2);
            }
            await task;
            Assert.IsTrue(sawWait, "die Wartephase wurde gezeigt");
            Assert.AreEqual(2, calls, "nach der Wartezeit wird einmal wiederholt");
            Assert.AreEqual(TestPhase.Idle, run.Status.Phase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task D_vision_run_shows_the_same_phases_and_counts()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            var seen = new List<TestPhase>();
            VisionTest? run = null;
            h.Respond = (model, _) =>
            {
                seen.Add(run!.Status.Phase);
                return Task.FromResult(model == "sees" ? Chat("It is red and blue.") : Chat("A cat."));
            };
            run = new VisionTest(Url, new[] { "sees", "blind", "text-embedding" }, Keys(dir), new HttpClient(h), null,
                now: () => new DateTime(2026, 10, 15, 12, 0, 0), timeout: TimeSpan.FromSeconds(5), pause: TimeSpan.FromMilliseconds(1),
                retryWait: TimeSpan.FromMilliseconds(50));
            await run.RunAsync();
            Assert.AreEqual(2, seen.Count);
            Assert.IsTrue(seen.All(p => p == TestPhase.WaitingFirstByte));
            Assert.AreEqual((1, 1, 1), run.Counts);
            Assert.AreEqual(TestPhase.Idle, run.Status.Phase);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Zeitgrenze und Notizen ──

    [TestMethod]
    public async Task E_a_slow_model_ends_as_timeout_and_the_note_says_slower_than_the_limit()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            h.Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Chat("never"); };
            var run = Classifier(h, new[] { "slow" }, dir, timeout: TimeSpan.FromMilliseconds(200));
            await run.RunAsync();
            var row = run.Results.Single();
            Assert.AreEqual(Strings.ClassifierStatusTimeout, row.Status);
            Assert.AreEqual(0.2, run.TimeoutSeconds, 1e-9);
            Assert.AreEqual(Strings.ClassifierNoteSlow("0.2"), TestNotes.Friendly(row.Status, row.Note, run.TimeoutSeconds));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void F_known_errors_get_a_short_text_and_others_keep_theirs()
    {
        Assert.AreEqual(Strings.ClassifierNoteNoImage,
            TestNotes.Friendly("400", "Received multimodal data but multimodal processing is not enabled", 60));
        Assert.AreEqual(Strings.ClassifierNoteBusy, TestNotes.Friendly("503", "ResourceExhausted: quota for this minute", 60));
        Assert.AreEqual(Strings.ClassifierNoteNotForAccount, TestNotes.Friendly("404", "Function abc Not found for account", 60));
        Assert.AreEqual(Strings.ClassifierNoteSlow("60"), TestNotes.Friendly(Strings.ClassifierStatusTimeout, "No complete answer", 60));
        Assert.IsNull(TestNotes.Friendly("200", "", 60), "eine gute Antwort braucht keine Übersetzung");
        Assert.IsNull(TestNotes.Friendly("400", "bad request", 60), "unbekannte 400 bleibt, wie sie ist");
        Assert.IsNull(TestNotes.Friendly("503", "overloaded", 60));
    }

    // ── Lauf-Zustand mit injizierter Uhr ──

    [TestMethod]
    public void G_phase_and_seconds_follow_the_clock()
    {
        var t = new DateTime(2026, 10, 15, 12, 0, 0);
        var st = new TestRunStatus(() => t);
        st.Begin(3);
        Assert.AreEqual(TestPhase.Idle, st.Phase);
        st.Next("alpha");
        Assert.AreEqual("alpha", st.Model);

        st.Enter(TestPhase.Sending);
        t = t.AddSeconds(2);
        st.Enter(TestPhase.WaitingFirstByte);
        Assert.AreEqual(2.0, st.RequestSeconds, 1e-9, "die Zeit der Anfrage läuft ab „sending“");
        t = t.AddSeconds(3);
        st.Enter(TestPhase.Receiving);
        Assert.AreEqual(5.0, st.RequestSeconds, 1e-9);
        Assert.AreEqual(0.0, st.PhaseSeconds, 1e-9, "die Phase Receiving begann eben");

        st.Enter(TestPhase.RateLimitWait, 20);
        Assert.AreEqual(TestPhase.RateLimitWait, st.Phase);
        Assert.AreEqual(20.0, st.WaitSeconds, 1e-9);
        Assert.AreEqual(0.0, st.RequestSeconds, 1e-9, "in der Wartezeit ist keine Anfrage offen");
        t = t.AddSeconds(4);
        Assert.AreEqual(4.0, st.PhaseSeconds, 1e-9);

        st.End();
        Assert.AreEqual(TestPhase.Idle, st.Phase);
        Assert.AreEqual("", st.Model);
    }

    [TestMethod]
    public void H_remaining_time_is_a_rough_estimate_from_the_measured_times()
    {
        var t = new DateTime(2026, 10, 15, 12, 0, 0);
        var st = new TestRunStatus(() => t);
        st.Begin(3);
        Assert.IsNull(st.RemainingSec(new double?[] { null, null }, 1), "ohne gemessene Zeit keine Schätzung");

        // Zwei gemessene Anfragen (10 s und 20 s, Schnitt 15 s), zwei noch offene Anfragen, eine davon läuft seit 5 s
        st.Next("next");
        st.Sent();
        st.Enter(TestPhase.Sending);
        t = t.AddSeconds(5);
        // Laufend: 15 − 5 = 10 s; zwei offene: je 15 s + 1 s Pause = 2 × 16 s
        Assert.AreEqual(42.0, st.RemainingSec(new double?[] { 10, 20, null }, 1)!.Value, 1e-9);
    }

    [TestMethod]
    public void I_text_of_the_live_line_is_short_and_plain()
    {
        Assert.AreEqual("waiting 20 s (rate limit)", Strings.TestPhaseText(TestPhase.RateLimitWait, 20));
        Assert.AreEqual("waiting for the first byte", Strings.TestPhaseText(TestPhase.WaitingFirstByte, 0));
        Assert.AreEqual("", Strings.TestPhaseText(TestPhase.Idle, 0));
        Assert.AreEqual("3 of 80 · ok 2 · failed 1 · skipped 0", Strings.ClassifierTestCounts(3, 80, 2, 1, 0));
        Assert.AreEqual("", Strings.ClassifierTestLeft(null));
        Assert.AreEqual("about 45 s left", Strings.ClassifierTestLeft(43));
        Assert.AreEqual("about 2 min 5 s left", Strings.ClassifierTestLeft(125));
    }
}
