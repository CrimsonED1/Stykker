using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N92 – Modelltests mit diesem PC als Quelle („This PC“ im Reiter Tests des Proxy-Dialogs): die laufenden lokalen Server werden
// ohne Schlüssel geprüft, jedes Modell an der Adresse seines Servers und unter dem Namen, den der Server erwartet. Die Zeilen tragen
// den Auswahlwert des Proxys, damit „Use for …“ und die Markierungen im Routing ihn direkt nehmen können. Ohne Netz.
[TestClass]
public class N92_LocalModelTestTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Uri, string Model, string? Auth)> Calls { get; } = new();
        public string Reply { get; set; } = "<block>no</block>";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            lock (Calls) Calls.Add((request.RequestUri!.ToString(), model, request.Headers.Authorization?.ToString()));
            var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Reply } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly Dictionary<string, LocalTestTarget> Local = new()
    {
        ["srv-a|C:/models/small.gguf"] = new LocalTestTarget("http://127.0.0.1:8081", "C:/models/small.gguf"),
        ["srv-b|llama3.2:3b"] = new LocalTestTarget("http://127.0.0.1:11434", "llama3.2:3b"),
    };

    [TestMethod]
    public async Task Safety_check_test_sends_each_local_model_to_its_own_server_without_a_key()
    {
        var h = new Recorder();
        var run = new ClassifierTest(ClassifierTest.LocalSource, Local.Keys, new ClassifierRequest(ClassifierTest.SampleBody(), false),
            keys: null, new HttpClient(h), timeout: TimeSpan.FromSeconds(5), pause: TimeSpan.FromMilliseconds(1), local: Local);
        await run.RunAsync();

        Assert.AreEqual("", run.Error);
        Assert.AreEqual(2, h.Calls.Count);
        CollectionAssert.AreEquivalent(new[] { "http://127.0.0.1:8081/v1/chat/completions", "http://127.0.0.1:11434/v1/chat/completions" },
            h.Calls.Select(c => c.Uri).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:/models/small.gguf", "llama3.2:3b" }, h.Calls.Select(c => c.Model).ToArray());
        Assert.IsTrue(h.Calls.All(c => c.Auth == null), "no Authorization header for local servers");
        // Die Zeilen tragen den Auswahlwert, nicht den Dateinamen
        CollectionAssert.AreEquivalent(Local.Keys.ToArray(), run.Results.Select(r => r.Model).ToArray());
        Assert.IsTrue(run.Results.All(r => r.FormatOk == true));
    }

    [TestMethod]
    public async Task Vision_test_uses_local_servers_too()
    {
        var h = new Recorder { Reply = "The left half is red, the right half is blue." };
        var run = new VisionTest(ClassifierTest.LocalSource, Local.Keys, keys: null, new HttpClient(h),
            timeout: TimeSpan.FromSeconds(5), pause: TimeSpan.FromMilliseconds(1), local: Local);
        await run.RunAsync();

        Assert.AreEqual("", run.Error);
        Assert.AreEqual(2, h.Calls.Count);
        Assert.IsTrue(h.Calls.All(c => c.Auth == null));
        Assert.IsTrue(run.Results.All(r => r.Ok));
    }

    [TestMethod]
    public async Task Cloud_test_without_local_targets_still_needs_a_key()
    {
        var h = new Recorder();
        var run = new ClassifierTest("https://cloud.example/v1", new[] { "m" }, new ClassifierRequest(ClassifierTest.SampleBody(), false),
            keys: null, new HttpClient(h), timeout: TimeSpan.FromSeconds(5), pause: TimeSpan.FromMilliseconds(1));
        await run.RunAsync();

        Assert.AreEqual(Strings.ProxyProviderKeyMissingShort, run.Error);
        Assert.AreEqual(0, h.Calls.Count);
    }

    [TestMethod]
    public void Test_source_prefers_the_choice_then_a_provider_with_key_then_this_pc()
    {
        var providers = new[] { ("https://a/v1", false), ("https://b/v1", true) };
        Assert.AreEqual(ClassifierTest.LocalSource, ProxyPanel.TestSource(providers, ClassifierTest.LocalSource));
        Assert.AreEqual("https://a/v1", ProxyPanel.TestSource(providers, "https://a/v1"));
        Assert.AreEqual("https://b/v1", ProxyPanel.TestSource(providers, null));
        Assert.AreEqual(ClassifierTest.LocalSource, ProxyPanel.TestSource(new[] { ("https://a/v1", false) }, null));
        Assert.AreEqual(ClassifierTest.LocalSource, ProxyPanel.TestSource(Array.Empty<(string, bool)>(), "https://gone/v1"));
    }

    [TestMethod]
    public void Choice_value_is_the_model_locally_and_the_cloud_key_otherwise()
    {
        Assert.AreEqual("srv-a|m", ProxyPanel.ChoiceValue(ClassifierTest.LocalSource, "srv-a|m"));
        Assert.AreEqual(ProxyTarget.CloudKey("https://a/v1", "m"), ProxyPanel.ChoiceValue("https://a/v1", "m"));
    }
}
