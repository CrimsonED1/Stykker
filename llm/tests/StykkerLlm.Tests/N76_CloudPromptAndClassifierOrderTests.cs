using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N76 – drei Dinge ohne Netz: die Reihenfolge des Modelltests (angehakte, bekannte Familien, Rest), die Modelle, die der Anbieter
// mit 404 abgelehnt hat (merken, überspringen, „Retry unavailable“), und das Ziel der Prompt-Seite für Cloud-Modelle über den
// Stykker-Proxy (Proxy-Adresse, Modellname, Proxy aus = Meldung).
[TestClass]
public class N76_CloudPromptAndClassifierOrderTests
{
    private const string Url = "https://cloud.example/v1";
    private const string Key = "sk-test-123";

    // Antwortet je Modell; merkt sich, welche Modelle in welcher Reihenfolge angefragt wurden
    private sealed class Scripted : HttpMessageHandler
    {
        public Func<string, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Answer("<block>no</block>"));
        public List<string> Models { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            lock (Models) Models.Add(model);
            return await Respond(model, ct);
        }
    }

    private static HttpResponseMessage Answer(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }),
            Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Error(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n76-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Ein Lauf im Ordner dir: Schlüssel dort hinterlegt, Pausen klein; dead = Datei der nicht verfügbaren Modelle
    private static ClassifierTest Make(Scripted h, IEnumerable<string> models, string dir, string? dead = null,
        IEnumerable<string>? offered = null, bool retry = false)
    {
        var keys = new ProviderKeys(dir, new FakePlatform { UserProtection = true });
        keys.Set(Url, Key);
        return new ClassifierTest(Url, models, new ClassifierRequest(ClassifierTest.SampleBody(), false), keys, new HttpClient(h),
            csvPath: null, timeout: TimeSpan.FromSeconds(5), pause: TimeSpan.FromMilliseconds(1), retryWait: TimeSpan.FromMilliseconds(50),
            offered: offered, deadPath: dead, retryUnavailable: retry);
    }

    [TestMethod]
    public void The_run_order_is_offered_then_known_families_in_list_order_then_the_rest()
    {
        var listed = new[]
        {
            "zeta-chat", "gemma-3-27b", "qwen3-b", "alpha", "nvidia/nemotron-x", "mistral-large", "llama-3.3-70b", "llama-4-scout",
            "kimi-k2", "beta-chat", "qwen2-a",
        };
        var order = ClassifierTest.RunOrder(listed, new[] { "zeta-chat" });
        // Angehakt zuerst; dann die Familien in der Reihenfolge der Liste (nvidia/, qwen, llama-3.3, llama-4, mistral, kimi, gemma-3),
        // innerhalb einer Familie alphabetisch; der Rest zuletzt, alphabetisch
        CollectionAssert.AreEqual(new[]
        {
            "zeta-chat", "nvidia/nemotron-x", "qwen2-a", "qwen3-b", "llama-3.3-70b", "llama-4-scout", "mistral-large", "kimi-k2",
            "gemma-3-27b", "alpha", "beta-chat",
        }, order);
    }

    [TestMethod]
    public async Task The_run_asks_the_offered_models_first()
    {
        var h = new Scripted();
        var dir = TempDir();
        try
        {
            var run = Make(h, new[] { "aa-plain", "zz-offered", "qwen-x" }, dir, offered: new[] { "zz-offered" });
            await run.RunAsync();
            CollectionAssert.AreEqual(new[] { "zz-offered", "qwen-x", "aa-plain" }, h.Models);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task A_model_that_answers_404_is_remembered_and_skipped_next_time_until_retried()
    {
        var h = new Scripted
        {
            Respond = (model, _) => Task.FromResult(model == "gone"
                ? Error(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Function 'x': Not found for account\"}}")
                : Answer("<block>no</block>")),
        };
        var dir = TempDir();
        var dead = Path.Combine(dir, ClassifierTest.DeadFile);
        try
        {
            var first = Make(h, new[] { "gone", "fast" }, dir, dead);
            await first.RunAsync();
            Assert.AreEqual("404", first.Results.Single(r => r.Model == "gone").Status);
            CollectionAssert.AreEqual(new[] { Url + "|gone" }, File.ReadAllLines(dead), "eine Zeile je Anbieter und Modell");

            h.Models.Clear();
            var second = Make(h, new[] { "gone", "fast" }, dir, dead);
            await second.RunAsync();
            CollectionAssert.AreEqual(new[] { "fast" }, h.Models.ToArray(), "das tote Modell bekommt keine Anfrage");
            var skipped = second.Results.Single(r => r.Model == "gone");
            Assert.AreEqual("skipped", skipped.Status);
            Assert.IsNull(skipped.FormatOk);
            Assert.AreEqual(Strings.ClassifierNoteUnavailableLast, skipped.Note);

            h.Models.Clear();
            var retry = Make(h, new[] { "gone", "fast" }, dir, dead, retry: true);
            await retry.RunAsync();
            CollectionAssert.Contains(h.Models, "gone", "„Retry unavailable“ fragt es wieder");
            Assert.AreEqual(1, File.ReadAllLines(dead).Length, "auch beim Wiederholen keine doppelte Zeile");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Only_the_answers_that_mean_unknown_model_are_remembered()
    {
        var h = new Scripted
        {
            Respond = (model, _) => Task.FromResult(model switch
            {
                "a-plain-404" => Error(HttpStatusCode.NotFound, "{\"error\":\"nope\"}"),
                "b-not-found-for-account" => Error(HttpStatusCode.InternalServerError, "Function 'x': Not found for account"),
                "c-server-error" => Error(HttpStatusCode.InternalServerError, "{\"error\":\"try later\"}"),
                "d-text-in-a-normal-answer" => Answer("404 page not found"),
                _ => Answer("<block>no</block>"),
            }),
        };
        var dir = TempDir();
        var dead = Path.Combine(dir, ClassifierTest.DeadFile);
        try
        {
            var run = Make(h, new[] { "a-plain-404", "b-not-found-for-account", "c-server-error", "d-text-in-a-normal-answer" }, dir, dead);
            await run.RunAsync();
            CollectionAssert.AreEqual(new[] { Url + "|a-plain-404", Url + "|b-not-found-for-account" }, File.ReadAllLines(dead));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task The_list_only_counts_for_its_own_provider_and_ignores_broken_lines()
    {
        var h = new Scripted();
        var dir = TempDir();
        var dead = Path.Combine(dir, ClassifierTest.DeadFile);
        try
        {
            File.WriteAllText(dead, "https://other.example/v1|x\r\n" + Url + "|y\r\nno-separator-here\r\n|empty-url\r\n");
            var run = Make(h, new[] { "x", "y", "z" }, dir, dead);
            await run.RunAsync();
            CollectionAssert.AreEqual(new[] { "x", "z" }, h.Models.ToArray(), "x gehört einem anderen Anbieter, y ist hier tot");
            Assert.AreEqual("skipped", run.Results.Single(r => r.Model == "y").Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void A_missing_dead_list_means_nothing_is_skipped()
    {
        var dir = TempDir();
        try
        {
            CollectionAssert.AreEqual(Array.Empty<string>(), ClassifierTest.DeadModels(Path.Combine(dir, "none.txt"), Url).ToArray());
            CollectionAssert.AreEqual(Array.Empty<string>(), ClassifierTest.DeadModels(null, Url).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void The_prompt_to_a_cloud_model_goes_to_the_proxy_with_the_name_the_proxy_knows()
    {
        var cloud = new ProxyChoice(ProxyTarget.CloudKey(Url, "model-a"), "Probe/model-a", "☁ Probe · model-a", true);
        var local = new ProxyChoice("sim-a", "model-l", "Local · model-l", false);
        var choices = new[] { local, cloud };

        var (target, error) = PromptTargets.Resolve(PromptTargets.ViaValue(cloud.Value), choices, 17500, proxyRunning: true);
        Assert.IsNull(error);
        Assert.AreEqual("http://127.0.0.1:17500", target!.Url, "die Adresse des lokalen Proxys, ohne /v1");
        Assert.AreEqual("Probe/model-a", target.Model, "der Name, unter dem der Proxy das Cloud-Modell kennt");

        var (current, currentError) = PromptTargets.Resolve(PromptTargets.ViaValue("stykker"), choices, 17500, proxyRunning: true);
        Assert.IsNull(currentError);
        Assert.AreEqual("stykker", current!.Model, "„current target“ schickt den virtuellen Namen des Proxys");
    }

    [TestMethod]
    public void With_the_proxy_off_the_prompt_says_so_and_has_no_target()
    {
        var cloud = new ProxyChoice(ProxyTarget.CloudKey(Url, "model-a"), "Probe/model-a", "☁ Probe · model-a", true);
        var (target, error) = PromptTargets.Resolve(PromptTargets.ViaValue(cloud.Value), new[] { cloud }, 17500, proxyRunning: false);
        Assert.IsNull(target);
        Assert.AreEqual(Strings.ClientProxyOff, error);
    }

    [TestMethod]
    public void The_group_has_the_current_target_and_the_cloud_models_only()
    {
        var cloud = new ProxyChoice(ProxyTarget.CloudKey(Url, "model-a"), "Probe/model-a", "☁ Probe · model-a", true);
        var local = new ProxyChoice("sim-a", "model-l", "Local · model-l", false);
        var options = PromptTargets.Options(new[] { local, cloud });
        CollectionAssert.AreEqual(new[] { PromptTargets.ViaValue("stykker"), PromptTargets.ViaValue(cloud.Value) }, options.Select(o => o.Value).ToArray());
        Assert.AreEqual(Strings.PromptViaCurrent, options[0].Label);
        Assert.AreEqual(cloud.Display, options[1].Label);
        Assert.IsFalse(PromptTargets.IsVia("sim-a"), "lokale Server bleiben ohne Präfix");

        // Ein Eintrag, den der Proxy nicht (mehr) anbietet, ist keine Stille: eine Meldung statt eines Ziels
        var (target, error) = PromptTargets.Resolve(PromptTargets.ViaValue("sim-a|model-l"), new[] { local, cloud }, 17500, proxyRunning: true);
        Assert.IsNull(target);
        Assert.AreEqual(Strings.PromptViaGone, error);
    }
}
