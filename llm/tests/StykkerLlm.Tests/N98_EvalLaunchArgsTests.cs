using StykkerLlm.Core;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Tests;

// N98 – Modelltests: ein von Hand eingetragenes Modell startet mit Modelldatei und Port aus den Feldern, wenn die
// Argumente sie nicht nennen (vorher startete llama-server ohne Modell und der Test wartete 8 Minuten auf /health).
[TestClass]
public class N98_EvalLaunchArgsTests
{
    [TestMethod]
    public void MissingModelAndPortComeFromTheFields()
    {
        var m = new EvalModelDef { Args = "-c 16384", ModelFile = @"C:\models\Bonsai 2B.gguf", Port = 8095 };
        var args = CmdLine.Split(m.LaunchArgs());
        var a = LlamaServerArgs.Parse(args);
        Assert.AreEqual(@"C:\models\Bonsai 2B.gguf", a.Model, "Pfad mit Leerzeichen bleibt ein Argument");
        Assert.AreEqual(8095, a.Port);
        Assert.AreEqual(16384, a.Ctx);
        CollectionAssert.Contains(args, "127.0.0.1");
    }

    [TestMethod]
    public void ArgumentsThatNameModelAndPortStayAsTheyAre()
    {
        const string full = "-m x.gguf --host 0.0.0.0 --port 9000 -c 4096";
        Assert.AreEqual(full, new EvalModelDef { Args = full, ModelFile = "y.gguf", Port = 8095 }.LaunchArgs());
        // Modell aus dem Netz (-hf): keine zusätzliche Modelldatei
        var hf = new EvalModelDef { Args = "-hf org/model --port 9001", ModelFile = "y.gguf", Port = 9001 }.LaunchArgs();
        Assert.IsFalse(hf.Contains("y.gguf"), hf);
    }
}
