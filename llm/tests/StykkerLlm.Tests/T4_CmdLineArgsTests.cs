using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class T4_CmdLineTests
{
    [TestMethod]
    public void Split_TabIsASeparator()
    {
        CollectionAssert.AreEqual(new[] { "p", "-m", "1" }, CmdLine.Split("p\t-m\t1"));
        CollectionAssert.AreEqual(new[] { "p", "a\tb" }, CmdLine.Split("p \"a\tb\""));
    }

    [TestMethod]
    public void Quote_SplitRoundTripForEachCase()
    {
        var cases = new[] { "a b", "with\ttab", "q\"uote", "C:\\dir\\", "trailing \\", "\\", "a\\b", "", "multi\nline", "C:\\Program Files\\llama-server.exe" };
        foreach (var c in cases)
            CollectionAssert.AreEqual(new[] { "p", c }, CmdLine.Split("p " + CmdLine.Quote(c)), "'" + c + "'");
    }

    [TestMethod]
    public void Quote_PlainArgsStayUntouched()
    {
        Assert.AreEqual("x", CmdLine.Quote("x"));
        Assert.AreEqual("a=b", CmdLine.Quote("a=b"));
        Assert.AreEqual("-m", CmdLine.Quote("-m"));
        Assert.AreEqual("\"\"", CmdLine.Quote(""));
    }

    [TestMethod]
    public void Redact_InlineValueMayContainEquals()
    {
        var (r, had) = CmdLine.Redact(new[] { "--api-key=a=b", "--hf-token=t=x", "-hft", "v" });
        Assert.IsTrue(had);
        CollectionAssert.AreEqual(new[] { "--api-key=***", "--hf-token=***", "-hft", "***" }, r);
        Assert.IsFalse(string.Join(' ', r).Contains("SECRET"));
    }

    [TestMethod]
    public void Redact_ManySecretsInMixedPositions()
    {
        var input = new[] { "-m", "x", "--api-key", "A", "-c", "1", "--api-key-file=B", "-hft", "C", "--hf-token=D", "positional" };
        var (r, had) = CmdLine.Redact(input);
        Assert.IsTrue(had);
        CollectionAssert.AreEqual(new[] { "-m", "x", "--api-key", "***", "-c", "1", "--api-key-file=***", "-hft", "***", "--hf-token=***", "positional" }, r);
    }

    [TestMethod]
    public void Redact_InlineShortSecretFlagIsRedacted()
    {
        var (r, had) = CmdLine.Redact(new[] { "-hft=SECRET" });
        Assert.IsTrue(had);
        CollectionAssert.AreEqual(new[] { "-hft=***" }, r);
    }

    [TestMethod]
    public void FilterEnvironment_SecretWhitelistVarsAreRedacted()
    {
        var env = new Dictionary<string, string>
        {
            ["LLAMA_ARG_TOKEN"] = "tok-SECRET", ["GGML_OPENAI_API_KEY"] = "sk-SECRET", ["HIP_TEST_PASSWORD"] = "pw",
            ["CUDA_VISIBLE_DEVICES"] = "0,1",
        };
        var (f, had) = CmdLine.FilterEnvironment(env);
        Assert.IsTrue(had);
        Assert.AreEqual("***", f["LLAMA_ARG_TOKEN"]);
        Assert.AreEqual("***", f["GGML_OPENAI_API_KEY"]);
        Assert.AreEqual("***", f["HIP_TEST_PASSWORD"]);
        Assert.AreEqual("0,1", f["CUDA_VISIBLE_DEVICES"]);
    }

    [TestMethod]
    public void FilterEnvironment_NonWhitelistedSecretsAreDroppedSilently()
    {
        var (f, had) = CmdLine.FilterEnvironment(new Dictionary<string, string> { ["MY_SECRET"] = "x", ["ANTHROPIC_API_KEY"] = "y", ["PASSWORD"] = "z" });
        Assert.AreEqual(0, f.Count);
        Assert.IsFalse(had);
    }

    [TestMethod]
    public void FilterEnvironment_WhitelistIsCaseInsensitive()
    {
        var (f, had) = CmdLine.FilterEnvironment(new Dictionary<string, string> { ["ggml_debug"] = "1", ["llama_arg_ctx_size"] = "4096" });
        Assert.AreEqual(2, f.Count);
        Assert.AreEqual("4096", f["llama_arg_ctx_size"]);
        Assert.IsFalse(had);
    }

    [TestMethod]
    public void FilterEnvironment_EmptyInputIsEmpty()
    {
        var (f, had) = CmdLine.FilterEnvironment(new Dictionary<string, string>());
        Assert.AreEqual(0, f.Count);
        Assert.IsFalse(had);
    }
}

[TestClass]
public class T4_LlamaArgsTests
{
    private static LlamaServerArgs Parse(string cmd, IReadOnlyDictionary<string, string>? env = null) =>
        LlamaServerArgs.Parse(CmdLine.Split(cmd).Skip(1).ToList(), env);

    [TestMethod]
    public void InlineEqualsFormsForKnownOptions()
    {
        var a = Parse("llama-server -m m.gguf --ctx-size=8192 --temp=0.7 --port=9099 --gpu-layers=99 --cache-type-k=q8_0 --mlock");
        Assert.AreEqual(8192, a.Ctx);
        Assert.AreEqual(0.7, a.Temp!.Value, 1e-9);
        Assert.AreEqual(9099, a.Port);
        Assert.AreEqual(99, a.Ngl);
        Assert.AreEqual("q8_0", a.CacheTypeK);
        Assert.IsTrue(a.Has("--mlock"));
        Assert.AreEqual(ServerMode.Normal, a.Mode);
        CollectionAssert.AreEqual(
            CmdLine.Split("-m m.gguf --ctx-size=8192 --temp=0.7 --port=9099 --gpu-layers=99 --cache-type-k=q8_0 --mlock").ToList(), a.ToArgs());
    }

    [TestMethod]
    public void GetAcceptsCanonicalShortAndLongSpelling()
    {
        var a = Parse("llama-server -c 4096 -ngl 5 --port 1");
        Assert.AreEqual("4096", a.Get("ctx"));
        Assert.AreEqual("4096", a.Get("-c"));
        Assert.AreEqual("4096", a.Get("--ctx-size"));
        Assert.AreEqual("5", a.Get("ngl"));
        Assert.AreEqual("1", a.Get("--port"));
    }

    [TestMethod]
    public void LastWinsAcrossNotations()
    {
        Assert.AreEqual(2, LlamaServerArgs.Parse(new[] { "--port", "1", "--port=2" }).Port);
        Assert.AreEqual(2, LlamaServerArgs.Parse(new[] { "--port=1", "--port", "2" }).Port);
    }

    [TestMethod]
    public void UnknownOptionFollowedByOptionStaysValueless()
    {
        var args = new[] { "-m", "x.gguf", "--my-tool", "-c", "1" };
        var a = LlamaServerArgs.Parse(args);
        Assert.AreEqual("", a.Get("--my-tool"));   // ohne Wert: leerer String, nicht null
        Assert.AreEqual(1, a.Ctx);
        CollectionAssert.AreEqual(args, a.ToArgs());
    }

    [TestMethod]
    public void UnknownOptionTakesNonOptionValueAndRoundTrips()
    {
        var args = new[] { "--custom-opt", "v", "--jinja", "-fa", "-1", "0.5" };
        var a = LlamaServerArgs.Parse(args);
        Assert.AreEqual("v", a.Get("--custom-opt"));
        Assert.IsTrue(a.Has("--jinja"));
        CollectionAssert.AreEqual(args, a.ToArgs());
    }

    [TestMethod]
    public void HuggingFaceModeWinsOverModel()
    {
        var a = LlamaServerArgs.Parse(new[] { "-m", "a.gguf", "-hf", "u/r:Q4" });
        Assert.AreEqual(ServerMode.HuggingFace, a.Mode);
        Assert.AreEqual("a.gguf", a.Model);
    }

    [TestMethod]
    public void EnvSuppliesHfRepoModelsDirAndFlashAttn()
    {
        var env = new Dictionary<string, string>
        {
            ["LLAMA_ARG_HF_REPO"] = "u/r", ["LLAMA_ARG_MODELS_DIR"] = "d", ["LLAMA_ARG_FLASH_ATTN"] = "off",
        };
        var a = LlamaServerArgs.Parse(new[] { "--port", "1" }, env);
        Assert.AreEqual("u/r", a.HfRepo);
        Assert.AreEqual("d", a.ModelsDir);
        Assert.AreEqual(ServerMode.HuggingFace, a.Mode);
        Assert.AreEqual(false, a.FlashAttn);
    }

    [TestMethod]
    public void BareFlagBeatsEnvDefault()
    {
        var env = new Dictionary<string, string> { ["LLAMA_ARG_FLASH_ATTN"] = "off" };
        var a = LlamaServerArgs.Parse(new[] { "-fa" }, env);
        Assert.AreEqual(true, a.FlashAttn);   // Schalter ohne Wert = an, Umgebung gilt nicht
    }
}
