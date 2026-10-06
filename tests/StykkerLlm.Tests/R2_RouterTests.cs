using System.Net;
using System.Net.Sockets;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Der „Stykker-Proxy": Routing nach Modell, Standardziel, /v1/models zusammengefasst, kein Ziel → 503.
[TestClass]
public class R2_RouterTests
{
    // Ein Backend, das seinen Namen zurückgibt und optional den empfangenen Anfragetext meldet
    private static RawUpstream Echo(string id, Action<byte[]>? onBody = null)
    {
        var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
        {
            onBody?.Invoke(req.Body);
            var payload = $"{{\"id\":\"{id}\"}}";
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
        };
        return up;
    }

    private static async Task<string> Post(int port, string path, string json)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
        return (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
    }

    private static async Task<string> Get(int port, string path)
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}{path}");
        return (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
    }

    [TestMethod]
    public async Task RoutesByModel_AggregatesModels_AndUsesTheDefaultTarget()
    {
        string? seenByA = null;
        using var a = Echo("A", b => seenByA = RouterProxy.ReadModel(b));
        using var b = Echo("B");
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget("ka", a.Url, "alpha", BackendKind.LlamaCpp, true, false),
            new ProxyTarget("kb", b.Url, "model-b", BackendKind.Ollama, true, false),
        }, "ka");
        proxy.Start();

        // /v1/models führt beide Backends und das virtuelle Modell zusammen
        var models = await Get(proxy.ListenPort, "/v1/models");
        StringAssert.Contains(models, "\"stykker\"");
        StringAssert.Contains(models, "\"alpha\"");
        StringAssert.Contains(models, "\"model-b\"");

        // nach Modell weiterleiten
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"model-b\",\"messages\":[]}"), "\"B\"");
        // „stykker" → Standardziel, model-Wert wird auf den Namen des Ziels gesetzt
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"stykker\",\"messages\":[]}"), "\"A\"");
        Assert.AreEqual("alpha", seenByA);
        // unbekanntes Modell → Standardziel
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"gibtsnicht\",\"messages\":[]}"), "\"A\"");
        // Anthropic /v1/messages mit model
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/messages", "{\"model\":\"model-b\",\"messages\":[]}"), "\"B\"");
    }

    [TestMethod]
    public async Task WithoutTarget_Answers503WithJson()
    {
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(Array.Empty<ProxyTarget>(), null);
        proxy.Start();
        var r = await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"stykker\",\"messages\":[]}");
        StringAssert.StartsWith(r, "503");
        StringAssert.Contains(r, "service_unavailable");
    }

    [TestMethod]
    public async Task OfflineTarget_Answers503()
    {
        using var a = Echo("A");
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[] { new ProxyTarget("ka", a.Url, "alpha", BackendKind.LlamaCpp, false, false) }, "ka");
        proxy.Start();
        var r = await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"stykker\",\"messages\":[]}");
        StringAssert.StartsWith(r, "503");
    }

    [TestMethod]
    public async Task LoadingTarget_IsWaitedFor()
    {
        using var a = Echo("A");
        using var proxy = new RouterProxy(0) { LoadingWait = TimeSpan.FromSeconds(10) };
        proxy.SetTargets(new[] { new ProxyTarget("ka", a.Url, "alpha", BackendKind.LlamaCpp, false, true) }, "ka");
        proxy.Start();
        var flip = Task.Run(async () =>
        {
            await Task.Delay(800);
            proxy.SetTargets(new[] { new ProxyTarget("ka", a.Url, "alpha", BackendKind.LlamaCpp, true, false) }, "ka");
        });
        var r = await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"alpha\",\"messages\":[]}");
        await flip;
        StringAssert.StartsWith(r, "200");
        StringAssert.Contains(r, "\"A\"");
    }

    [TestMethod]
    public async Task LoadingTargetThatNeverBecomesReady_Answers503()
    {
        using var a = Echo("A");
        using var proxy = new RouterProxy(0) { LoadingWait = TimeSpan.FromSeconds(1) };
        proxy.SetTargets(new[] { new ProxyTarget("ka", a.Url, "alpha", BackendKind.LlamaCpp, false, true) }, "ka");
        proxy.Start();
        var r = await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"alpha\",\"messages\":[]}");
        StringAssert.StartsWith(r, "503");
    }

    [TestMethod]
    public void RewriteModel_OnlyTouchesTheModelField()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"stykker\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}");
        var rewritten = Encoding.UTF8.GetString(RouterProxy.RewriteModel(body, "alpha"));
        StringAssert.Contains(rewritten, "\"model\":\"alpha\"");
        StringAssert.Contains(rewritten, "\"stream\":true");
        StringAssert.Contains(rewritten, "\"content\":\"hi\"");
        Assert.AreEqual("alpha", RouterProxy.ReadModel(Encoding.UTF8.GetBytes(rewritten)));
    }
}
