using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Einrichtungs-Assistent für Cloud-Anbieter (OpenRouter): mit dem Schlüssel die Modelle abfragen, ankreuzen, und nur die
// gewählten anbieten. Ohne Netz: der Anbieter ist ein FakeHandler, der Schlüssel liegt in einem Testordner (FakePlatform).
[TestClass]
public class N59_CloudWizardTests
{
    private const string Url = "https://openrouter.ai/api/v1";
    private const string Route = "openrouter.ai/api/v1/models";
    private const string Models = "{\"data\":[{\"id\":\"openai/gpt-4o\"},{\"id\":\"meta/llama\"},{\"id\":\"mistral/small\"}]}";

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-n59-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProxyManager Proxy(FakeHandler fake, ProviderKeys keys) =>
        new(() => Array.Empty<ServerWatcher>(), new AppSettings(), _ => { }, http: new HttpClient(fake), keys: keys);

    private static ProviderKeys Keys(string dir) => new(dir, new FakePlatform { UserProtection = true });

    [TestMethod]
    public void OfferedModels_WithoutChoice_OffersEverything_WithChoice_OnlyTheChosen()
    {
        var fetched = new[] { "a", "b", "c" };
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, ProxyManager.OfferedModels(fetched, null).ToArray());
        CollectionAssert.AreEqual(new[] { "b" }, ProxyManager.OfferedModels(fetched, new[] { "B", "x" }).ToArray(), "Groß-/Kleinschreibung egal");
    }

    [TestMethod]
    public void AddProvider_WithChosenModels_KeepsOnlyTheDistinctNames()
    {
        using var pm = Proxy(new FakeHandler(), Keys(TempDir()));
        pm.AddProvider("OpenRouter", Url, "sk-test", out var error, new[] { "x", " x ", "", "y" });
        Assert.IsNull(error);
        CollectionAssert.AreEqual(new[] { "x", "y" }, pm.EnabledModelsFor(Url)!.ToArray());
    }

    [TestMethod]
    public void AddProvider_WithAnEmptyChoice_IsRefused_AndAddsNothing()
    {
        using var pm = Proxy(new FakeHandler(), Keys(TempDir()));
        pm.AddProvider("OpenRouter", Url, "sk-test", out var error, Array.Empty<string>());
        Assert.AreEqual(Strings.ProxyProviderNoModelsChosen, error);
        Assert.AreEqual(0, pm.Providers.Count);
    }

    [TestMethod]
    public void SetProviderModels_SavesTheChoice()
    {
        using var pm = Proxy(new FakeHandler(), Keys(TempDir()));
        pm.AddProvider("OpenRouter", Url, "sk-test", out _);
        pm.SetProviderModels(Url, new[] { "openai/gpt-4o" }, out var error);
        Assert.IsNull(error);
        CollectionAssert.AreEqual(new[] { "openai/gpt-4o" }, pm.EnabledModelsFor(Url)!.ToArray());
    }

    [TestMethod]
    public void SetProviderModels_RefusesAnEmptyChoice()
    {
        using var pm = Proxy(new FakeHandler(), Keys(TempDir()));
        pm.AddProvider("OpenRouter", Url, "sk-test", out _);
        pm.SetProviderModels(Url, Array.Empty<string>(), out var error);
        Assert.AreEqual(Strings.ProxyProviderNoModelsChosen, error);
        Assert.IsNull(pm.EnabledModelsFor(Url), "die alte Auswahl (alle) bleibt");
    }

    [TestMethod]
    public async Task ProbeProvider_ReadsTheModelList_AndStoresNothing()
    {
        var fake = new FakeHandler();
        fake.Routes[Route] = (HttpStatusCode.OK, Models);
        using var pm = Proxy(fake, Keys(TempDir()));
        var (models, error) = await pm.ProbeProviderAsync(Url, "sk-test");
        Assert.IsNull(error);
        CollectionAssert.AreEqual(new[] { "openai/gpt-4o", "meta/llama", "mistral/small" }, models);
        Assert.AreEqual("Bearer sk-test", fake.AuthHeaders[0]);
        Assert.AreEqual(0, pm.Providers.Count, "nichts eingetragen");
        Assert.IsFalse(pm.HasProviderKey(Url), "der Schlüssel wird nicht gespeichert");
    }

    [TestMethod]
    public async Task ProbeProvider_WrongKey_ReportsTheStatus()
    {
        var fake = new FakeHandler();
        fake.Routes[Route] = (HttpStatusCode.Unauthorized, "{}");
        using var pm = Proxy(fake, Keys(TempDir()));
        var (models, error) = await pm.ProbeProviderAsync(Url, "wrong");
        Assert.AreEqual(0, models.Length);
        Assert.AreEqual(Strings.ProxyProviderModelError(401), error);
    }

    [TestMethod]
    public async Task ProbeProvider_WithoutKeyInTheField_UsesTheStoredKey()
    {
        var keys = Keys(TempDir());
        keys.Set(Url, "stored-key");
        var fake = new FakeHandler();
        fake.Routes[Route] = (HttpStatusCode.OK, Models);
        using var pm = Proxy(fake, keys);
        var (models, error) = await pm.ProbeProviderAsync(Url, "");
        Assert.IsNull(error);
        Assert.AreEqual(3, models.Length);
        Assert.AreEqual("Bearer stored-key", fake.AuthHeaders[0]);
    }

    [TestMethod]
    public async Task ProbeProvider_WithoutAnyKey_RefusesBeforeAnyRequest()
    {
        var fake = new FakeHandler();
        using var pm = Proxy(fake, Keys(TempDir()));
        var (models, error) = await pm.ProbeProviderAsync(Url, null);
        Assert.AreEqual(0, models.Length);
        Assert.AreEqual(Strings.ProxyProviderKeyMissing, error);
        Assert.AreEqual(0, fake.Requests.Count, "ohne Schlüssel geht keine Anfrage ins Netz");
    }

    [TestMethod]
    public async Task ProbeProvider_BadUrl_IsRefused()
    {
        var fake = new FakeHandler();
        using var pm = Proxy(fake, Keys(TempDir()));
        var (_, error) = await pm.ProbeProviderAsync("ftp://openrouter.ai/api/v1", "sk-test");
        Assert.AreEqual(Strings.ProxyProviderUrlInvalid, error);
        Assert.AreEqual(0, fake.Requests.Count);
    }

    [TestMethod]
    public void UsableModelsPath_OnlyOpenRouter_HasAKeyList()
    {
        Assert.AreEqual("/models/user", ProxyManager.UsableModelsPath(Url));
        Assert.AreEqual("/models/user", ProxyManager.UsableModelsPath("https://eu.openrouter.ai/api/v1"));
        Assert.IsNull(ProxyManager.UsableModelsPath("https://api.example.com/v1"));
        Assert.IsNull(ProxyManager.UsableModelsPath("https://openrouter.ai.example.com/api/v1"), "ein fremder Host, der nur so beginnt");
        Assert.IsNull(ProxyManager.UsableModelsPath("https://notopenrouter.ai/api/v1"), "kein Unterhost von openrouter.ai");
        Assert.IsNull(ProxyManager.UsableModelsPath("kein link"));
    }

    [TestMethod]
    public async Task UsableModels_ReadsTheKeyList_WithTheKey()
    {
        var fake = new FakeHandler();
        fake.Routes["openrouter.ai/api/v1/models/user"] = (HttpStatusCode.OK, "{\"data\":[{\"id\":\"openai/gpt-4o\"}],\"total_count\":1}");
        using var pm = Proxy(fake, Keys(TempDir()));
        var (models, failed) = await pm.UsableModelsAsync(Url, "sk-test");
        Assert.IsFalse(failed);
        CollectionAssert.AreEqual(new[] { "openai/gpt-4o" }, models!);
        Assert.AreEqual("Bearer sk-test", fake.AuthHeaders[0]);
        Assert.AreEqual("openrouter.ai/api/v1/models/user", fake.Requests[0]);
    }

    [TestMethod]
    public async Task UsableModels_AnEmptyList_IsAKnownNone()
    {
        var fake = new FakeHandler();
        fake.Routes["openrouter.ai/api/v1/models/user"] = (HttpStatusCode.OK, "{\"data\":[]}");
        using var pm = Proxy(fake, Keys(TempDir()));
        var (models, failed) = await pm.UsableModelsAsync(Url, "sk-test");
        Assert.IsFalse(failed);
        Assert.AreEqual(0, models!.Length);
    }

    [TestMethod]
    public async Task UsableModels_AFailedRequest_IsUnknown_NotEmpty()
    {
        var fake = new FakeHandler();
        fake.Routes["openrouter.ai/api/v1/models/user"] = (HttpStatusCode.Forbidden, "{}");
        using var pm = Proxy(fake, Keys(TempDir()));
        var (models, failed) = await pm.UsableModelsAsync(Url, "sk-test");
        Assert.IsNull(models);
        Assert.IsTrue(failed);
    }

    [TestMethod]
    public async Task UsableModels_WithoutAListOrAKey_AsksNothing()
    {
        var fake = new FakeHandler();
        using var pm = Proxy(fake, Keys(TempDir()));
        var (other, otherFailed) = await pm.UsableModelsAsync("https://api.example.com/v1", "sk-test");
        Assert.IsNull(other);
        Assert.IsFalse(otherFailed);
        var (none, noneFailed) = await pm.UsableModelsAsync(Url, null);
        Assert.IsNull(none);
        Assert.IsFalse(noneFailed);
        Assert.AreEqual(0, fake.Requests.Count, "ohne Liste oder ohne Schlüssel geht keine Anfrage ins Netz");
    }

    [TestMethod]
    public void OrderForWizard_UsableFirst_CatalogOrderKept()
    {
        var all = new[] { "a", "b", "c", "d" };
        CollectionAssert.AreEqual(new[] { ("a", true), ("c", true), ("b", false), ("d", false) }, ProxyManager.OrderForWizard(all, new[] { "C", "A" }).ToArray(), "Groß-/Kleinschreibung egal");
        CollectionAssert.AreEqual(new[] { ("a", false), ("b", false), ("c", false), ("d", false) }, ProxyManager.OrderForWizard(all, null).ToArray(), "ohne Angabe: alle gleich");
        CollectionAssert.AreEqual(new[] { ("c", true), ("z", true), ("a", false), ("b", false), ("d", false) }, ProxyManager.OrderForWizard(all, new[] { "c", "z" }).ToArray(), "eine nutzbare ID, die im Katalog fehlt, kommt auch nach oben");
    }
}
