using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Monitor-Extras: Client-Blöcke der Proxy-Karte, Kontextgrenze je Profil (ContextFit) und der Modellfilter der Anfragen
[TestClass]
public class N96_MonitorExtrasTests
{
    private const string Lan = "http://192.0.2.20:17500/v1";
    private const string Loopback = "http://127.0.0.1:17500/v1";

    // Modell mit 32 Schichten, 8 KV-Köpfen, Kopfgröße 128 (4096 / 32): f16 ergibt 128 KiB je Token
    private static GgufInfo Info(long size = 4L << 30, long? ctxLen = null) => new()
    {
        Architecture = "a", FileSize = size,
        Meta = MetaOf(ctxLen, ("a.block_count", 32L), ("a.embedding_length", 4096L), ("a.attention.head_count", 32L), ("a.attention.head_count_kv", 8L)),
    };

    private static IReadOnlyDictionary<string, object> MetaOf(long? ctxLen, params (string Key, object Value)[] items)
    {
        var d = items.ToDictionary(i => i.Key, i => i.Value);
        if (ctxLen is long c) d["a.context_length"] = c;
        return d;
    }

    private static string Text(string baseUrl, ClientSnippets.Shell shell, int block) => ClientSnippets.Build(baseUrl, shell)[block].Text;

    // ── Client-Blöcke ──

    [TestMethod]
    public void ClaudeCodeGetsRootWithoutV1AndTheProxyNames()
    {
        var ps = Text(Lan, ClientSnippets.Shell.PowerShell, 0);
        Assert.IsTrue(ps.Contains("$env:ANTHROPIC_BASE_URL = \"http://192.0.2.20:17500\"\n"), ps);
        Assert.IsFalse(ps.Contains("17500/v1"), ps);
        Assert.IsTrue(ps.Contains("$env:ANTHROPIC_AUTH_TOKEN = \"stykker\""), ps);
        Assert.IsTrue(ps.Contains("$env:ANTHROPIC_MODEL = \"claude-stykker\""), ps);
    }

    [TestMethod]
    public void OpenAiBlockKeepsV1AndUsesStykkerModel()
    {
        var ps = Text(Lan, ClientSnippets.Shell.PowerShell, 1);
        Assert.IsTrue(ps.Contains("$env:OPENAI_BASE_URL = \"http://192.0.2.20:17500/v1\""), ps);
        Assert.IsTrue(ps.Contains("$env:OPENAI_API_KEY = \"stykker\""), ps);
        Assert.IsTrue(ps.Contains("$env:OPENAI_MODEL = \"stykker\""), ps);
    }

    [TestMethod]
    public void BashFormUsesExportAndLoopbackAddress()
    {
        var sh = Text(Loopback, ClientSnippets.Shell.Bash, 0);
        Assert.IsTrue(sh.Contains("export ANTHROPIC_BASE_URL=\"http://127.0.0.1:17500\""), sh);
        Assert.IsTrue(sh.Contains("export ANTHROPIC_MODEL=\"claude-stykker\""), sh);
        Assert.IsFalse(sh.Contains("$env:"), sh);
        var openAi = Text(Loopback, ClientSnippets.Shell.Bash, 1);
        Assert.IsTrue(openAi.Contains("export OPENAI_BASE_URL=\"http://127.0.0.1:17500/v1\""), openAi);
    }

    [TestMethod]
    public void CurlBlockPostsToChatCompletionsWithStykkerModel()
    {
        var curl = Text(Loopback, ClientSnippets.Shell.PowerShell, 2);
        Assert.IsTrue(curl.StartsWith("curl http://127.0.0.1:17500/v1/chat/completions "), curl);
        Assert.IsTrue(curl.Contains("\"model\":\"stykker\""), curl);
        Assert.IsTrue(curl.Contains("Authorization: Bearer stykker"), curl);
    }

    [TestMethod]
    public void SnippetsNeverContainAProviderKey()
    {
        foreach (var url in new[] { Lan, Loopback })
            foreach (var shell in new[] { ClientSnippets.Shell.PowerShell, ClientSnippets.Shell.Bash })
                foreach (var block in ClientSnippets.Build(url, shell))
                {
                    var t = block.Text;
                    Assert.IsFalse(t.Contains("sk-"), t);
                    Assert.IsFalse(t.Contains("nvapi"), t);
                    Assert.IsFalse(t.Contains("Bearer ") && !t.Contains("Bearer stykker"), t);
                }
    }

    [TestMethod]
    public void RootStripsOnlyATrailingV1()
    {
        Assert.AreEqual("http://h:1", ClientSnippets.Root("http://h:1/v1"));
        Assert.AreEqual("http://h:1", ClientSnippets.Root("http://h:1/v1/"));
        Assert.AreEqual("http://h:1", ClientSnippets.Root("http://h:1"));
    }

    // ── Kontext je Profil ──

    [TestMethod]
    public void KvCacheIs128KiBPerTokenForF16()
    {
        double bytes = ContextFit.KvBytesPerToken(Info(), "f16", "f16")!.Value;
        Assert.AreEqual(131072.0, bytes, 0.5);
    }

    [TestMethod]
    public void Q8CacheIsAboutHalfOfF16()
    {
        // q8_0 speichert 34 Byte je 32 Werte (1,0625 statt 2): knapp über der Hälfte, nicht genau die Hälfte
        double f16 = ContextFit.KvBytesPerToken(Info(), "f16", "f16")!.Value;
        double q8 = ContextFit.KvBytesPerToken(Info(), "q8_0", "q8_0")!.Value;
        double ratio = q8 / f16;
        Assert.IsTrue(ratio >= 0.5 && ratio <= 0.55, ratio.ToString());
    }

    [TestMethod]
    public void MaxCtxRoundsDownToWholeKAndFitsFreeVram()
    {
        // 8 GB frei − 4 GB Modell − 0,6 GB Puffer = 3,4 GB = 27 852,8 Token à 128 KiB → 27 Ki
        int? ctx = ContextFit.MaxCtx(Info(), 8.0, null, "f16", "f16");
        Assert.AreEqual(27 * 1024, ctx);
        Assert.AreEqual(0, ctx!.Value % ContextFit.Step);
    }

    [TestMethod]
    public void MaxCtxIsCappedByTheModelsContextLength()
    {
        Assert.AreEqual(4096, ContextFit.MaxCtx(Info(ctxLen: 4096), 8.0, null, "f16", "f16"));
    }

    [TestMethod]
    public void Q8CacheFitsMoreContext()
    {
        int f16 = ContextFit.MaxCtx(Info(), 8.0, null, "f16", "f16")!.Value;
        int q8 = ContextFit.MaxCtx(Info(), 8.0, null, "q8_0", "q8_0")!.Value;
        Assert.IsTrue(q8 > f16, $"q8={q8} f16={f16}");
    }

    [TestMethod]
    public void NoFreeVramOrModelTooBigGivesZero()
    {
        Assert.AreEqual(0, ContextFit.MaxCtx(Info(), 0, null, "f16", "f16"));
        Assert.AreEqual(0, ContextFit.MaxCtx(Info(), 3.0, null, "f16", "f16"));   // das Modell allein passt nicht
    }

    [TestMethod]
    public void MissingGgufValuesGiveNull()
    {
        // ohne Schichtenzahl kein KV-Cache zu rechnen
        var noBlocks = new GgufInfo { Architecture = "a", FileSize = 4L << 30, Meta = new Dictionary<string, object> { ["a.embedding_length"] = 4096L } };
        Assert.IsNull(ContextFit.MaxCtx(noBlocks, 8.0));
        // ohne Dateigröße kein Modellbedarf
        Assert.IsNull(ContextFit.MaxCtx(Info(size: 0), 8.0));
        // ohne Köpfe und ohne Embedding-Größe ist der KV-Cache 0 → nichts anzeigen
        var noHeads = new GgufInfo { Architecture = "a", FileSize = 4L << 30, Meta = new Dictionary<string, object> { ["a.block_count"] = 32L } };
        Assert.IsNull(ContextFit.MaxCtx(noHeads, 8.0));
    }

    // ── Modellfilter der Anfragen ──

    private static RequestRow Row(string model) =>
        new(model, "key|" + model, null, model, DateTime.Now, 1, 0, 0, 10, "", "cli", null, null, "ok");

    [TestMethod]
    public void ModelFilterListsOnlySeenModelsSortedAndDistinct()
    {
        var rows = new[] { Row("beta"), Row("alpha"), Row("beta"), Row("") };
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, RequestRows.Models(rows).ToList());
    }

    [TestMethod]
    public void ModelFilterKeepsOnlyThatModel()
    {
        var rows = new[] { Row("beta"), Row("alpha"), Row("beta") };
        var only = RequestRows.ForModel(rows, "beta");
        Assert.AreEqual(2, only.Count);
        Assert.IsTrue(only.All(r => r.Model == "beta"));
    }

    [TestMethod]
    public void ModelFilterEmptyOrUnknownShowsAll()
    {
        var rows = new[] { Row("beta"), Row("alpha") };
        Assert.AreEqual(2, RequestRows.ForModel(rows, "").Count);
        Assert.AreEqual(2, RequestRows.ForModel(rows, null).Count);
        Assert.AreEqual(2, RequestRows.ForModel(rows, "gone").Count);
    }

    // ── Texte ──

    [TestMethod]
    public void CtxTextShowsWholeThousands()
    {
        Assert.AreEqual("fits up to ctx ~27 k", Strings.MxCtxFit(27 * 1024));
    }

    [TestMethod]
    public void RouteTextNamesTheRoutesAndAbsentProxy()
    {
        Assert.AreEqual(Strings.MxNoRoute, Strings.MxRouteText(null));
        Assert.AreEqual(Strings.MxRouteDefault, Strings.MxRouteText(ProxyRoutes.Default));
        Assert.AreEqual(Strings.ReqRouteAuto, Strings.MxRouteText(ProxyRoutes.Classifier));
    }
}
