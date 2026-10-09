using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class CmdLineTests
{
    [TestMethod]
    public void Split_SimpleAndQuotedProgram()
    {
        CollectionAssert.AreEqual(new[] { "C:\\Program Files\\x.exe", "-m", "a b", "--port", "1" },
            CmdLine.Split("\"C:\\Program Files\\x.exe\" -m \"a b\" --port 1"));
        CollectionAssert.AreEqual(new[] { "llama-server", "-c", "10" }, CmdLine.Split("  llama-server   -c 10  "));
    }

    [TestMethod]
    public void Split_BackslashAndQuoteRules()
    {
        // 2n Backslashes + " -> n Backslashes, Umschalter
        CollectionAssert.AreEqual(new[] { "p", "a b\\", "x" }, CmdLine.Split("p \"a b\\\\\" x"));            // 2 Backslashes + " -> 1 Backslash, Anführungszeichen schließt
        CollectionAssert.AreEqual(new[] { "p", "a\\b c" }, CmdLine.Split("p \"a\\b c\""));              // einzelner Backslash ohne folgendes " bleibt
        // 2n+1 Backslashes + " -> wörtliches "
        CollectionAssert.AreEqual(new[] { "p", "say \"hi\"" }, CmdLine.Split("p \"say \\\"hi\\\"\""));
        // Backslashes ohne folgendes " bleiben
        CollectionAssert.AreEqual(new[] { "p", "C:\\dir\\file" }, CmdLine.Split("p C:\\dir\\file"));
        // "" in Anführungszeichen = wörtliches "
        CollectionAssert.AreEqual(new[] { "p", "a\"b" }, CmdLine.Split("p \"a\"\"b\""));
        // leerer Parameter
        CollectionAssert.AreEqual(new[] { "p", "", "x" }, CmdLine.Split("p \"\" x"));
        // Pfad mit Backslash vor schließendem Anführungszeichen
        CollectionAssert.AreEqual(new[] { "p", "C:\\dir\\" }, CmdLine.Split("p \"C:\\dir\\\\\""));
    }

    [TestMethod]
    public void Split_EmptyAndNull()
    {
        Assert.AreEqual(0, CmdLine.Split(null).Count);
        Assert.AreEqual(0, CmdLine.Split("   ").Count);
    }

    [TestMethod]
    public void QuoteJoin_RoundTrips()
    {
        var args = new[] { "C:\\Program Files\\a b.exe", "plain", "", "with space", "q\"uote", "trail\\", "trail space\\", "a\\\"b" };
        var back = CmdLine.Split(CmdLine.Join(args));
        CollectionAssert.AreEqual(args, back);
    }

    [TestMethod]
    public void Redact_SeparateAndInlineSecrets()
    {
        var (r, had) = CmdLine.Redact(new[] { "-m", "x.gguf", "--api-key", "sk-SECRET", "--hf-token=hf_SECRET", "-hft", "t", "--api-key-file", "C:\\k.txt", "--port", "1" });
        Assert.IsTrue(had);
        CollectionAssert.AreEqual(new[] { "-m", "x.gguf", "--api-key", "***", "--hf-token=***", "-hft", "***", "--api-key-file", "***", "--port", "1" }, r);
        Assert.IsFalse(string.Join(' ', r).Contains("SECRET"));
    }

    [TestMethod]
    public void Redact_NothingToHide()
    {
        var (r, had) = CmdLine.Redact(new[] { "-m", "x", "-c", "1" });
        Assert.IsFalse(had);
        Assert.AreEqual(4, r.Count);
    }

    [TestMethod]
    public void Redact_TrailingSecretOptionWithoutValue()
    {
        var (r, had) = CmdLine.Redact(new[] { "--api-key" });
        Assert.IsTrue(had);
        Assert.AreEqual(1, r.Count);
    }

    [TestMethod]
    public void FilterEnvironment_WhitelistAndSecrets()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "x", ["CUDA_VISIBLE_DEVICES"] = "0", ["GGML_CUDA_NO_PINNED"] = "1", ["LLAMA_ARG_CTX_SIZE"] = "4096",
            ["LLAMA_ARG_API_KEY"] = "sk-SECRET", ["HIP_VISIBLE_DEVICES"] = "1", ["USERNAME"] = "me",
        };
        var (f, had) = CmdLine.FilterEnvironment(env);
        Assert.IsTrue(had);
        Assert.AreEqual(5, f.Count);
        Assert.AreEqual("***", f["LLAMA_ARG_API_KEY"]);
        Assert.AreEqual("4096", f["LLAMA_ARG_CTX_SIZE"]);
        Assert.IsFalse(f.ContainsKey("PATH"));
        Assert.IsFalse(f.ContainsKey("USERNAME"));
    }
}
