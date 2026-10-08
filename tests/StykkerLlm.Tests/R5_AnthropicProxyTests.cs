using System.Net;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// R5 – Der Stykker-Proxy als Anthropic-Gateway: /v1/messages übersetzen, im Strom ausgeben, Fehler weiterreichen, mitschreiben
[TestClass]
public class R5_AnthropicProxyTests
{
    private const string AnthropicBody = """
        {"model":"claude-stykker","max_tokens":512,"stream":true,
         "system":"Sei knapp.",
         "messages":[{"role":"user","content":"Sag hallo"}]}
        """;

    private static async Task<string> PostMessages(int port, string json)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"http://127.0.0.1:{port}/v1/messages", new StringContent(json, Encoding.UTF8, "application/json"));
        return (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
    }

    private static RouterProxy ProxyAt(RawUpstream up)
    {
        var proxy = new RouterProxy(0);
        proxy.SetTargets(new[] { new ProxyTarget("ka", up.Url, "alpha", BackendKind.LlamaCpp, true, false) }, "ka");
        proxy.Start();
        return proxy;
    }

    private static string OpenAiStream(params string[] chunks) => string.Concat(chunks.Select(c => c + "\n\n"));

    [TestMethod]
    public async Task Messages_AreTranslated_Streamed_AndRecorded()
    {
        string? sent = null;
        using var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
        {
            sent = Encoding.UTF8.GetString(req.Body);
            var sse = OpenAiStream(
                "data: {\"id\":\"1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Ich\"}}]}",
                "data: {\"id\":\"1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" Gruesse\"}}]}",
                "data: {\"id\":\"1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}",
                "data: {\"id\":\"1\",\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3}}",
                "data: [DONE]");
            await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n" + sse, ct);
        };
        using var proxy = ProxyAt(up);
        var records = new List<ProxyRecord>();
        proxy.Recorded += r => { lock (records) records.Add(r); };

        var answer = await PostMessages(proxy.ListenPort, AnthropicBody);

        // die Antwort kommt als Anthropic-Ereignisfolge an
        StringAssert.StartsWith(answer, "200");
        StringAssert.Contains(answer, "event: message_start");
        StringAssert.Contains(answer, "\"type\":\"text_delta\",\"text\":\"Ich\"");
        StringAssert.Contains(answer, "\"type\":\"text_delta\",\"text\":\" Gruesse\"");
        StringAssert.Contains(answer, "\"stop_reason\":\"end_turn\"");
        StringAssert.Contains(answer, "\"output_tokens\":3");
        StringAssert.EndsWith(answer.TrimEnd(), "data: {\"type\":\"message_stop\"}");

        // das Ziel hat eine OpenAI-Anfrage bekommen: System-Nachricht, Nutzertext, Zielmodell, Stream
        Assert.IsNotNull(sent, "das Ziel wurde nicht erreicht");
        StringAssert.Contains(sent, "\"model\":\"alpha\"");
        StringAssert.Contains(sent, "\"stream\":true");
        StringAssert.Contains(sent, "\"role\":\"system\"");
        StringAssert.Contains(sent, "Sei knapp.");
        StringAssert.Contains(sent, "Sag hallo");
        StringAssert.Contains(sent, "\"max_tokens\":512");

        // die Anfrage wird wie jede andere aufgezeichnet
        for (int i = 0; i < 40 && records.Count == 0; i++) await Task.Delay(50);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("/v1/messages", records[0].Path);
        Assert.AreEqual("stop", records[0].FinishReason);
        Assert.AreEqual(200, records[0].HttpStatus);
        Assert.IsFalse(records[0].ClientAborted);
    }

    [TestMethod]
    public async Task TargetWithoutStream_IsWrappedIntoEvents()
    {
        using var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
        {
            const string json = "{\"id\":\"1\",\"model\":\"alpha\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Fertig\"}}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1}}";
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}", ct);
        };
        using var proxy = ProxyAt(up);

        var answer = await PostMessages(proxy.ListenPort, AnthropicBody);

        StringAssert.Contains(answer, "event: message_start");
        StringAssert.Contains(answer, "\"type\":\"text_delta\",\"text\":\"Fertig\"");
        StringAssert.Contains(answer, "\"stop_reason\":\"end_turn\"");
        StringAssert.Contains(answer, "event: message_stop");
    }

    [TestMethod]
    public async Task TargetError_IsForwardedAsAnthropicError()
    {
        using var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
        {
            const string json = "{\"error\":{\"message\":\"context too small\",\"type\":\"invalid_request_error\"}}";
            await RawUpstream.Send(s, $"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}", ct);
        };
        using var proxy = ProxyAt(up);

        var answer = await PostMessages(proxy.ListenPort, AnthropicBody);

        StringAssert.StartsWith(answer, "400");
        StringAssert.Contains(answer, "\"type\":\"invalid_request_error\"");
        StringAssert.Contains(answer, "context too small");
    }

    [TestMethod]
    public async Task UnreachableTarget_AnswersAnthropicError502()
    {
        var up = new RawUpstream();
        string url = up.Url;
        up.Dispose();
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[] { new ProxyTarget("ka", url, "alpha", BackendKind.LlamaCpp, true, false) }, "ka");
        proxy.Start();

        var answer = await PostMessages(proxy.ListenPort, AnthropicBody);

        StringAssert.StartsWith(answer, "502");
        StringAssert.Contains(answer, "\"type\":\"error\"");
    }

    [TestMethod]
    public async Task WithoutTarget_AnswersAnthropicError503()
    {
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(Array.Empty<ProxyTarget>(), null);
        proxy.Start();

        var answer = await PostMessages(proxy.ListenPort, AnthropicBody);

        StringAssert.StartsWith(answer, "503");
        StringAssert.Contains(answer, "\"type\":\"error\"");
    }
}