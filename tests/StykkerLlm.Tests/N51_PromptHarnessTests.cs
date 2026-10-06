using System.Net;
using System.Text;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Mini-Harness (Knopf „Prompt“): Stream wie llama-server, Antwort ohne Stream, Fehler, Abbruch, Verlauf
[TestClass]
public class N51_PromptHarnessTests
{
    // Antwortet auf /v1/chat/completions mit dem gegebenen Körper; merkt sich die Anfrage
    private sealed class ChatHandler(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<string?> Auth { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            Auth.Add(request.Headers.Authorization?.ToString());
            return answer(body);
        }
    }

    private static HttpResponseMessage Sse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var e in events) sb.Append("data: ").Append(e).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static string Delta(string? content = null, string? reasoning = null, string? finish = null) =>
        "{\"choices\":[{\"index\":0,\"delta\":{" +
        string.Join(",", new[] { content == null ? null : "\"content\":" + JsonSerializer.Serialize(content), reasoning == null ? null : "\"reasoning_content\":" + JsonSerializer.Serialize(reasoning) }.Where(x => x != null)) +
        "},\"finish_reason\":" + (finish == null ? "null" : "\"" + finish + "\"") + "}]}";

    [TestMethod]
    public async Task Stream_ThinkingAndAnswerApart_UsageAndServerSpeed()
    {
        var h = new ChatHandler(_ => Sse(
            Delta(reasoning: "Rayleigh "), Delta(reasoning: "scattering."),
            Delta(content: "Blue "), Delta(content: "light."), Delta(finish: "stop"),
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":21,\"completion_tokens\":9},\"timings\":{\"predicted_per_second\":87.5}}"));
        using var http = new HttpClient(h);
        var seen = new StringBuilder();
        var r = await PromptHarness.RunAsync(http, "http://127.0.0.1:8081/", new[] { new ChatTurn("user", "Why is the sky blue?") },
            new PromptOptions { System = "Be short.", Temperature = 0.3, MaxTokens = 64, Model = "qwen3-8b" }, (t, _) => seen.Append(t));

        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual("Blue light.", r.Text);
        Assert.AreEqual("Rayleigh scattering.", r.Reasoning);
        Assert.AreEqual("Blue light.", seen.ToString(), "die Antwort kommt Stück für Stück");
        Assert.AreEqual(21, r.PromptTokens);
        Assert.AreEqual(9, r.CompletionTokens);
        Assert.AreEqual(87.5, r.Tps, "die Messung des Servers gewinnt");
        Assert.AreEqual("stop", r.Finish);
        StringAssert.Contains(PromptHarness.Stats(r), "21 → 9 tok");

        using var doc = JsonDocument.Parse(h.Bodies.Single());
        var b = doc.RootElement;
        Assert.IsTrue(b.GetProperty("stream").GetBoolean());
        Assert.AreEqual("qwen3-8b", b.GetProperty("model").GetString());
        Assert.AreEqual(64, b.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("system", b.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.AreEqual("Why is the sky blue?", b.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task WithoutStream_TheSimulatorAnswerIsReadWhole()
    {
        var world = new SimWorld(seed: 1);
        var sim = world.Add(new SimServerSpec
        {
            Kind = BackendKind.LlamaCpp, Name = "sim", Model = "model-a", Context = 8192, Slots = 1, TpsMin = 50, TpsMax = 60, ModelGb = 4,
        });
        var url = NetAddr.Url("127.0.0.1", sim.Port);
        using var http = new HttpClient(new SimHandler(world));
        var r = await PromptHarness.RunAsync(http, url, new[] { new ChatTurn("user", "Hi") }, new PromptOptions { MaxTokens = 500 });
        Assert.IsTrue(r.Ok, r.Error);
        StringAssert.Contains(r.Text, "sky looks blue");
        Assert.IsTrue(r.CompletionTokens > 0);
        Assert.IsTrue(r.Tps > 0);
    }

    [TestMethod]
    public async Task Errors_SaySomethingUseful()
    {
        using var needsKey = new HttpClient(new ChatHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var a = await PromptHarness.RunAsync(needsKey, "http://x:1", new[] { new ChatTurn("user", "x") }, new PromptOptions { ApiKey = "geheim" });
        Assert.AreEqual(Strings.PromptNeedsKey, a.Error);

        var h = new ChatHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"message\":\"the request exceeds the available context size\"}}") });
        using var bad = new HttpClient(h);
        var b = await PromptHarness.RunAsync(bad, "http://x:1", new[] { new ChatTurn("user", "x") }, new PromptOptions { ApiKey = "geheim" });
        Assert.AreEqual(Strings.PromptHttpError(400, "the request exceeds the available context size"), b.Error);
        Assert.AreEqual("Bearer geheim", h.Auth.Single(), "der Schlüssel geht nur als Kopf mit");

        using var gone = new HttpClient(new ChatHandler(_ => throw new HttpRequestException("connection refused")));
        var c = await PromptHarness.RunAsync(gone, "http://x:1", new[] { new ChatTurn("user", "x") }, new PromptOptions());
        StringAssert.Contains(c.Error, "connection refused");
    }

    [TestMethod]
    public async Task Session_KeepsTheConversation_AndDropsAQuestionWithoutAnswer()
    {
        int calls = 0;
        var h = new ChatHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("loading model") }
            : Sse(Delta(content: "Answer " + calls), Delta(finish: "stop")));
        using var http = new HttpClient(h);
        var session = new PromptSession();

        var first = await session.SendAsync(http, "http://x:1", "Hello");
        Assert.IsFalse(first.Ok);
        Assert.AreEqual(0, session.Turns.Count, "ohne Antwort bleibt die Frage nicht im Verlauf");

        await session.SendAsync(http, "http://x:1", "Hello");
        await session.SendAsync(http, "http://x:1", "And now?");
        Assert.AreEqual(4, session.Turns.Count);
        Assert.AreEqual("Answer 3", session.Turns[^1].Content);
        using var doc = JsonDocument.Parse(h.Bodies[^1]);
        Assert.AreEqual(3, doc.RootElement.GetProperty("messages").GetArrayLength(), "die Nachfrage schickt das Gespräch mit");

        session.Clear();
        Assert.AreEqual(0, session.Turns.Count);
        Assert.IsNull(session.Last);
    }

    [TestMethod]
    public async Task Stop_EndsTheStream_AndKeepsWhatCameSoFar()
    {
        var h = new ChatHandler(body =>
        {
            // Ein Stream, der nach dem ersten Stück hängt (Stopp-Knopf muss ihn beenden)
            var pipe = new System.IO.Pipelines.Pipe();
            _ = Task.Run(async () =>
            {
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("data: " + Delta(content: "Once upon") + "\n\n"));
                await Task.Delay(Timeout.Infinite);
            });
            var content = new StreamContent(pipe.Reader.AsStream());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(h);
        using var cts = new CancellationTokenSource();
        var r = await PromptHarness.RunAsync(http, "http://x:1", new[] { new ChatTurn("user", "Tell a story") }, new PromptOptions(),
            (t, _) => { if (t.Length > 0) cts.CancelAfter(50); }, cts.Token);
        Assert.AreEqual("stopped", r.Finish);
        Assert.AreEqual("Once upon", r.Text);
        Assert.IsTrue(r.Ok);
    }

    [TestMethod]
    public void ModelFor_PrefersTheLoadedModel()
    {
        var ollama = new RemoteServer { Model = "–" };
        Assert.AreEqual("", PromptHarness.ModelFor(ollama));
        ollama.Models.Add(new LoadedModel("llama3.2:3b", 0, 0, null, null, "", ""));
        Assert.AreEqual("llama3.2:3b", PromptHarness.ModelFor(ollama));
        Assert.AreEqual("bonsai", PromptHarness.ModelFor(new RemoteServer { Model = "bonsai" }));
    }
}
