using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class LlamaArgsTests
{
    private static LlamaServerArgs Parse(string cmd, IReadOnlyDictionary<string, string>? env = null) =>
        LlamaServerArgs.Parse(CmdLine.Split(cmd).Skip(1).ToList(), env);

    [TestMethod]
    public void ParsesBonsaiCommandLine()
    {
        var a = Parse(Samples.BonsaiCmd);
        Assert.AreEqual("C:\\ai\\bonsai\\models\\Bonsai-27B-Q1_0.gguf", a.Model);
        Assert.AreEqual(8081, a.Port);
        Assert.AreEqual("127.0.0.1", a.Host);
        Assert.AreEqual("bonsai-27b-1bit", a.Alias);
        Assert.AreEqual(65536, a.Ctx);
        Assert.AreEqual(99, a.Ngl);
        Assert.AreEqual(1, a.Np);
        Assert.AreEqual("q4_0", a.CacheTypeK);
        Assert.AreEqual("q4_0", a.CacheTypeV);
        Assert.AreEqual(true, a.FlashAttn);
        Assert.AreEqual(true, a.Jinja);
        Assert.AreEqual(1.0, a.Temp);
        Assert.AreEqual(0.95, a.TopP);
        Assert.AreEqual(20, a.TopK);
        Assert.AreEqual(0.05, a.MinP);
        Assert.AreEqual("C:\\ai\\bonsai\\server-1bit.log", a.LogFile);
        Assert.AreEqual(ServerMode.Normal, a.Mode);
    }

    [TestMethod]
    public void RoundTripKeepsUnknownParametersInOrder()
    {
        var args = new[] { "-m", "x.gguf", "--weird-flag", "--unknown-opt", "value", "--port=9000", "-fa", "--jinja", "positional", "-c", "4096" };
        var a = LlamaServerArgs.Parse(args);
        CollectionAssert.AreEqual(args, a.ToArgs());
        Assert.AreEqual(9000, a.Port);
        Assert.AreEqual(4096, a.Ctx);
        Assert.AreEqual(true, a.FlashAttn);   // -fa ohne Wert, --jinja gehört nicht dazu
        Assert.IsTrue(a.Has("--weird-flag"));
        Assert.AreEqual("value", a.Get("--unknown-opt"));
    }

    [TestMethod]
    public void FlashAttnOffAndOptionalSwitch()
    {
        Assert.AreEqual(false, LlamaServerArgs.Parse(new[] { "-fa", "off" }).FlashAttn);
        Assert.AreEqual(true, LlamaServerArgs.Parse(new[] { "--flash-attn", "auto" }).FlashAttn);
        Assert.IsNull(LlamaServerArgs.Parse(new[] { "-m", "x" }).FlashAttn);
    }

    [TestMethod]
    public void LastValueWinsAndJinjaToggle()
    {
        var a = LlamaServerArgs.Parse(new[] { "--port", "1", "--port", "2", "--jinja", "--no-jinja" });
        Assert.AreEqual(2, a.Port);
        Assert.AreEqual(false, a.Jinja);
    }

    [TestMethod]
    public void NegativeNumbersAreValues()
    {
        var a = LlamaServerArgs.Parse(new[] { "--seed", "-1", "-c", "10" });
        Assert.AreEqual("-1", a.Get("--seed"));
        Assert.AreEqual(10, a.Ctx);
    }

    [TestMethod]
    public void EnvironmentSuppliesDefaults()
    {
        var env = new Dictionary<string, string> { ["LLAMA_ARG_PORT"] = "9999", ["LLAMA_ARG_CTX_SIZE"] = "2048", ["LLAMA_ARG_MODEL"] = "m.gguf" };
        var a = LlamaServerArgs.Parse(new[] { "-c", "4096" }, env);
        Assert.AreEqual(9999, a.Port);
        Assert.AreEqual(4096, a.Ctx);   // Parameter schlägt Umgebung
        Assert.AreEqual("m.gguf", a.Model);
    }

    [TestMethod]
    public void ModeDetection()
    {
        Assert.AreEqual(ServerMode.Router, LlamaServerArgs.Parse(new[] { "--port", "8080" }).Mode);
        Assert.AreEqual(ServerMode.Router, LlamaServerArgs.Parse(new[] { "--models-dir", "d" }).Mode);
        Assert.AreEqual(ServerMode.HuggingFace, LlamaServerArgs.Parse(new[] { "-hf", "user/repo:Q4" }).Mode);
        Assert.AreEqual(ServerMode.Normal, LlamaServerArgs.Parse(new[] { "--model", "x.gguf" }).Mode);
    }

    [TestMethod]
    public void NglAutoIsKeptRaw()
    {
        var a = LlamaServerArgs.Parse(new[] { "-ngl", "all" });
        Assert.IsNull(a.Ngl);
        Assert.AreEqual("all", a.NglRaw);
    }

    [TestMethod]
    public void ModelNameFromBothSlashStyles()
    {
        Assert.AreEqual("Bonsai-27B-Q1_0", ServerInfo.ModelName("C:\\a\\Bonsai-27B-Q1_0.gguf"));
        Assert.AreEqual("m", ServerInfo.ModelName("/home/x/m.GGUF"));
    }
}
