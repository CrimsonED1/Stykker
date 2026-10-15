using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N75 – Modelltest der Sicherheitsprüfung („Test classifier models“ im Proxy-Dialog): je Modell eines Anbieters eine Prüfanfrage,
// gemessen werden Zeit bis zum ersten Byte, Gesamtzeit und ob die Antwort mit <block> beginnt. Ohne Netz: scripted Handler.
[TestClass]
public class N75_ClassifierTestTests
{
    private const string Url = "https://cloud.example/v1";
    private const string Key = "sk-test-123";

    // Antwortet je Modell (Name, Anfragetext, Abbruchtoken); merkt sich, welche Modelle wie angefragt wurden
    private sealed class Scripted : HttpMessageHandler
    {
        public Func<string, string, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _, _) => Task.FromResult(Answer("<block>no</block>"));
        public List<string> Models { get; } = new();
        public List<string?> Auth { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            lock (Models) { Models.Add(model); Auth.Add(request.Headers.Authorization?.ToString()); Bodies.Add(body); }
            return await Respond(model, body, ct);
        }
    }

    // Eine Chat-Antwort mit dem Text (und optional einem Denk-Text, der nicht zählen darf)
    private static HttpResponseMessage Answer(string content, string? reasoning = null)
    {
        object message = reasoning == null ? new { content } : new { content, reasoning_content = reasoning };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message } } }), Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Error(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n75-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Ein Test mit dem Schlüssel im Ordner dir (DPAPI-Ersatz des Testplattform), kleine Zeiten für die Pausen
    private static ClassifierTest Make(Scripted h, IEnumerable<string> models, string dir, string? csv = null,
        TimeSpan? timeout = null, TimeSpan? retryWait = null, string key = Key)
    {
        var keys = new ProviderKeys(dir, new FakePlatform { UserProtection = true });
        keys.Set(Url, key);
        return new ClassifierTest(Url, models, new ClassifierRequest(ClassifierTest.SampleBody(), false), keys, new HttpClient(h),
            csv, timeout: timeout ?? TimeSpan.FromSeconds(5), pause: TimeSpan.FromMilliseconds(1), retryWait: retryWait ?? TimeSpan.FromMilliseconds(50));
    }

    [TestMethod]
    public async Task A_fast_model_with_a_block_answer_is_ok()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "fast-model" }, dir);
            await run.RunAsync();
            var r = run.Results.Single();
            Assert.AreEqual("200", r.Status);
            Assert.AreEqual(true, r.FormatOk);
            Assert.AreEqual("", r.Note);
            Assert.IsNotNull(r.FirstByteSec);
            Assert.IsNotNull(r.TotalSec);
            Assert.AreEqual("Bearer " + Key, h.Auth.Single(), "der Schlüssel geht nur als Authorization-Kopf hinaus");
            // Denken aus, kein Stream, Stoppzeichen gesetzt – wie es die Sicherheitsprüfung braucht
            var sent = JsonNode.Parse(h.Bodies.Single())!;
            Assert.IsFalse(sent["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
            Assert.AreEqual("</block>", sent["stop"]!.AsArray()[0]!.GetValue<string>());
            Assert.IsNull(sent["stream"]);
            Assert.IsFalse(run.Running);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task A_wrong_format_is_not_ok_and_says_why()
    {
        var h = new Scripted { Respond = (_, _, _) => Task.FromResult(Answer("Sure, the answer is no.")) };
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "chatty" }, dir);
            await run.RunAsync();
            var r = run.Results.Single();
            Assert.AreEqual("200", r.Status, "die Antwort kam, nur im falschen Format");
            Assert.AreEqual(false, r.FormatOk);
            StringAssert.StartsWith(r.Note, "The answer does not start with <block>");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Leading_spaces_are_allowed_but_reasoning_text_does_not_count()
    {
        var h = new Scripted
        {
            Respond = (model, _, _) => Task.FromResult(model == "spaced"
                ? Answer("  \n<block>no</block>")
                : Answer("", reasoning: "<block>no</block>")),
        };
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "spaced", "thinker" }, dir);
            await run.RunAsync();
            var rows = run.Results.ToDictionary(r => r.Model);
            Assert.AreEqual(true, rows["spaced"].FormatOk);
            Assert.AreEqual(false, rows["thinker"].FormatOk, "nur der Antworttext zählt, nicht reasoning_content");
            Assert.AreEqual(Strings.ClassifierNoteEmpty, rows["thinker"].Note);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task An_error_status_gives_the_providers_message_as_note()
    {
        var h = new Scripted
        {
            Respond = (_, _, _) => Task.FromResult(Error(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"model not found\"}}")),
        };
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "gone" }, dir);
            await run.RunAsync();
            var r = run.Results.Single();
            Assert.AreEqual("404", r.Status);
            Assert.AreEqual(false, r.FormatOk);
            Assert.AreEqual("model not found", r.Note);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task A_slow_model_times_out()
    {
        var h = new Scripted
        {
            Respond = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Answer("<block>no</block>"); },
        };
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "slow" }, dir, timeout: TimeSpan.FromMilliseconds(300));
            var clock = Stopwatch.StartNew();
            await run.RunAsync();
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(10), "die Grenze greift, ohne auf das Modell zu warten");
            var r = run.Results.Single();
            Assert.AreEqual("timeout", r.Status);
            Assert.AreEqual(false, r.FormatOk);
            Assert.IsFalse(run.Running);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Models_that_are_not_chat_models_are_skipped_by_name()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            var models = new[] { "text-embedding-3-large", "llama-guard-3-8b", "whisper-large-v3", "bge-m3", "qwen2-vl-7b-instruct", "fast-model" };
            var run = Make(h, models, dir);
            await run.RunAsync();
            CollectionAssert.AreEqual(new[] { "qwen2-vl-7b-instruct", "fast-model" }, h.Models, "übersprungene Namen bekommen keine Anfrage");
            var rows = run.Results;
            Assert.AreEqual(6, rows.Count);
            Assert.AreEqual(4, rows.Count(r => r.Status == "skipped"));
            Assert.IsTrue(rows.Where(r => r.Status == "skipped").All(r => r.FormatOk == null && r.Note.Contains("Not asked")));
            Assert.IsNull(ClassifierTest.SkipWord("qwen2-vl-7b-instruct"), "Bildmodelle bleiben im Test");
            Assert.AreEqual("guard", ClassifierTest.SkipWord("llama-guard-3-8b"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task A_rate_limit_is_retried_once_and_no_more()
    {
        var counts = new Dictionary<string, int>();
        var h = new Scripted
        {
            Respond = (model, _, _) =>
            {
                int n;
                lock (counts) n = counts[model] = counts.GetValueOrDefault(model) + 1;
                if (model == "busy-once" && n == 1) return Task.FromResult(Error((HttpStatusCode)429, "{\"error\":\"slow down\"}"));
                if (model == "busy-always") return Task.FromResult(Error((HttpStatusCode)429, "{\"error\":\"slow down\"}"));
                return Task.FromResult(Answer("<block>no</block>"));
            },
        };
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "busy-once", "busy-always" }, dir);
            await run.RunAsync();
            var rows = run.Results.ToDictionary(r => r.Model);
            Assert.AreEqual("200", rows["busy-once"].Status);
            Assert.AreEqual(true, rows["busy-once"].FormatOk);
            StringAssert.Contains(rows["busy-once"].Note, "Rate limited");
            Assert.AreEqual("429", rows["busy-always"].Status);
            Assert.AreEqual(false, rows["busy-always"].FormatOk);
            Assert.AreEqual(2, counts["busy-once"]);
            Assert.AreEqual(2, counts["busy-always"], "nach dem einen Wiederholen ist Schluss");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Stopping_keeps_the_rows_measured_so_far()
    {
        var h = new Scripted
        {
            Respond = async (model, _, ct) =>
            {
                if (model == "first") return Answer("<block>no</block>");
                await Task.Delay(Timeout.Infinite, ct);   // das zweite Modell antwortet nie
                return Answer("<block>no</block>");
            },
        };
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "first", "second", "third" }, dir, timeout: TimeSpan.FromSeconds(30));
            using var cts = new CancellationTokenSource();
            var task = run.RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (run.Results.Count < 1 && DateTime.UtcNow < deadline) await Task.Delay(10);
            cts.Cancel();
            await task;
            Assert.IsFalse(run.Running);
            var rows = run.Results;
            Assert.AreEqual(1, rows.Count, "das abgebrochene und die folgenden Modelle fehlen, das Gemessene bleibt");
            Assert.AreEqual("first", rows[0].Model);
            Assert.AreEqual(1, h.Models.Count(m => m == "first"));
            Assert.IsFalse(h.Models.Contains("third"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void The_latest_real_check_is_the_test_request_otherwise_the_example()
    {
        var dir = TempDir();
        try
        {
            var check = "{{\"model\":\"claude-x\",\"max_tokens\":{0},\"stop_sequences\":[\"</block>\"],\"messages\":[]}}";
            var chat = "{\"model\":\"claude-x\",\"max_tokens\":32000,\"tools\":[{\"name\":\"Bash\"}],\"stop_sequences\":[\"</block>\"],\"messages\":[]}";
            File.WriteAllText(Path.Combine(dir, "20260101-000000-000-a.json"), string.Format(check, 1111));
            File.WriteAllText(Path.Combine(dir, "20260102-000000-000-b.json"), chat);
            File.WriteAllText(Path.Combine(dir, "20260103-000000-000-c.json"), string.Format(check, 2222));
            File.WriteAllText(Path.Combine(dir, "20260104-000000-000-d.json"), chat);   // neuer, aber keine Prüfung

            var pick = ClassifierTest.PickRequest(dir);
            Assert.IsTrue(pick.Real);
            StringAssert.Contains(Encoding.UTF8.GetString(pick.Body), "2222", "die neueste Prüfung gewinnt");

            var none = ClassifierTest.PickRequest(Path.Combine(dir, "missing"));
            Assert.IsFalse(none.Real);
            Assert.IsTrue(RouterProxy.IsSafetyCheck(none.Body), "das Beispiel hat die Form der Prüfung");
            Assert.IsTrue(none.Body.Length > 150_000, "Fülltext wie bei Claude Code");

            var onlyChats = Path.Combine(dir, "chats");
            Directory.CreateDirectory(onlyChats);
            File.WriteAllText(Path.Combine(onlyChats, "20260101-000000-000-x.json"), chat);
            Assert.IsFalse(ClassifierTest.PickRequest(onlyChats).Real);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task The_key_never_reaches_a_note_or_the_csv()
    {
        const string secret = "sk-secret-987";
        var h = new Scripted
        {
            Respond = (_, _, _) => Task.FromResult(Error(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"invalid key " + secret + "\"}}")),
        };
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "classifier-test.csv");
            var run = Make(h, new[] { "echo" }, dir, csv: csv, key: secret);
            await run.RunAsync();
            var r = run.Results.Single();
            Assert.AreEqual("401", r.Status);
            Assert.IsFalse(r.Note.Contains(secret), "auch ein Anbieter, der den Schlüssel zurückschreibt, verrät ihn nicht");
            StringAssert.Contains(r.Note, "***");
            var text = File.ReadAllText(csv);
            Assert.IsFalse(text.Contains(secret));
            StringAssert.Contains(text, "Provider");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task The_csv_gets_one_header_and_later_runs_append()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "classifier-test.csv");
            await Make(h, new[] { "one" }, dir, csv: csv).RunAsync();
            await Make(h, new[] { "two" }, dir, csv: csv).RunAsync();
            var lines = File.ReadAllLines(csv);
            Assert.AreEqual("Time,Provider,Model,Status,FirstByteSec,TotalSec,Format ok,Note", lines[0]);
            Assert.AreEqual(3, lines.Length, "eine Kopfzeile, eine Zeile je Lauf");
            StringAssert.Contains(lines[1], ",one,200,");
            StringAssert.Contains(lines[2], ",two,200,");
            StringAssert.Contains(lines[2], ",yes,");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void The_table_puts_usable_fastest_first_and_skipped_last()
    {
        var t = new DateTime(2026, 10, 15);
        var rows = new[]
        {
            new ClassifierResult(t, "skipped-model", "skipped", null, null, null, "Not asked"),
            new ClassifierResult(t, "ok-slow", "200", 1.0, 1.5, true, ""),
            new ClassifierResult(t, "timeout-model", "timeout", null, 60, false, "No complete answer"),
            new ClassifierResult(t, "wrong-fast", "200", 0.1, 0.2, false, "format"),
            new ClassifierResult(t, "ok-fast", "200", 0.2, 0.5, true, ""),
        };
        CollectionAssert.AreEqual(new[] { "ok-fast", "ok-slow", "wrong-fast", "timeout-model", "skipped-model" },
            ClassifierTest.Sorted(rows).Select(r => r.Model).ToArray());
    }

    [TestMethod]
    public void The_sample_request_has_the_shape_of_a_safety_check()
    {
        var body = ClassifierTest.SampleBody();
        Assert.IsTrue(RouterProxy.IsSafetyCheck(body));
        // Im JSON steht „<“ als <; gelesen wird der Text, nicht die Bytes
        var question = JsonNode.Parse(body)!["messages"]!.AsArray()[0]!["content"]!.GetValue<string>();
        StringAssert.Contains(question, "<block>no</block>");
    }

    [TestMethod]
    public async Task Starting_without_a_key_is_refused_and_nothing_is_sent()
    {
        using var host = new SimHost(new List<SimServerSpec>
        {
            new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "sim-a", Model = "model-a", Context = 8192, Slots = 1, RequestsPerMinute = 60, ModelGb = 4 },
        }, seed: 3, autoStep: false);
        var e = host.Engine;
        e.Settings.ProxyProviders.Add(new ProxyProvider { Name = "Probe", BaseUrl = Url });
        var handler = new Scripted();
        e.ClassifierClient = () => new HttpClient(handler);
        var ctx = new ActionContext { Engine = e, Launcher = new LaunchCoordinator(e, new RemotePrompt()) };

        var r = await ActionApi.ExecuteAsync(new ActionRequest { Action = "classifier.test.start", Arg = Url }, ctx, new RemotePrompt());
        Assert.IsFalse(r.Ok);
        Assert.AreEqual(Strings.ClassifierTestNoKey, r.Message);
        Assert.AreEqual(0, handler.Models.Count, "ohne Schlüssel geht keine einzige Anfrage hinaus");
        Assert.IsNull(e.ClassifierRun);

        var unknown = await ActionApi.ExecuteAsync(new ActionRequest { Action = "classifier.test.start", Arg = "https://other.example/v1" }, ctx, new RemotePrompt());
        Assert.IsFalse(unknown.Ok);
        Assert.AreEqual(Strings.ProxyProviderUnknown, unknown.Message);
        Assert.IsFalse(e.StopClassifierTest(), "nichts läuft, nichts zu stoppen");
    }
}
