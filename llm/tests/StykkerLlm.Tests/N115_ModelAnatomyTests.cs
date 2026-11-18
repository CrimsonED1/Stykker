using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N115 – Ansicht „Model“: Experten aus dem GGUF-Kopf, MoE-Schalter der Kommandozeile und wo jede Schicht liegt
// (ModelAnatomyBuilder), dazu die Phase eines Slots. Keine echten Modelle: kleine GGUF-Köpfe im Speicher.
[TestClass]
public class N115_ModelAnatomyTests
{
    // Ein MoE-Modell wie Qwen3-30B-A3B im Kleinen: 48 Schichten, 128 Experten, 8 je Token, die erste Schicht dicht
    private static GgufInfo Moe(int layers = 48, int dense = 0) => Gguf.Read(new MemoryStream(new GgufBuilder()
        .Text("general.architecture", "qwen3moe")
        .U32("qwen3moe.block_count", (uint)layers)
        .U32("qwen3moe.expert_count", 128)
        .U32("qwen3moe.expert_used_count", 8)
        .U32("qwen3moe.leading_dense_block_count", (uint)dense)
        .Tensor("blk.0.attn_q.weight", new long[] { 1000 }, 0)
        .Tensor("blk.0.ffn_up_exps.weight", new long[] { 128, 100 }, 0)
        .Tensor("blk.0.ffn_down_exps.weight", new long[] { 128, 100 }, 0)
        .Tensor("blk.0.ffn_up_shexp.weight", new long[] { 600 }, 0)
        .Build()));

    private static GgufInfo Dense() => Gguf.Read(new MemoryStream(new GgufBuilder()
        .Text("general.architecture", "llama").U32("llama.block_count", 32).Tensor("blk.0.attn_q.weight", new long[] { 10 }, 0).Build()));

    private static LlamaServerArgs Args(params string[] a) => LlamaServerArgs.Parse(a);

    [TestMethod]
    public void TheGgufHeadCountsTheRoutedExpertsButNotTheSharedOnes()
    {
        var g = Moe();
        Assert.IsTrue(g.IsMoe);
        Assert.AreEqual(128, g.ExpertCount);
        Assert.AreEqual(8, g.ExpertUsedCount);
        Assert.AreEqual(1000 + 25600 + 600, g.ParameterCount);
        Assert.AreEqual(25600, g.ExpertParameterCount);
        Assert.IsFalse(Dense().IsMoe);
        Assert.AreEqual(0, Dense().ExpertParameterCount);
    }

    [TestMethod]
    public void ExpertTensorNamesFollowTheLlamaCppPattern()
    {
        Assert.IsTrue(Gguf.IsRoutedExpertTensor("blk.3.ffn_up_exps.weight"));
        Assert.IsTrue(Gguf.IsRoutedExpertTensor("blk.3.ffn_gate_up_exps.weight"));
        Assert.IsTrue(Gguf.IsRoutedExpertTensor("blk.3.ffn_down_chexps.weight"));
        Assert.IsFalse(Gguf.IsRoutedExpertTensor("blk.3.ffn_up_shexp.weight"));
        Assert.IsFalse(Gguf.IsRoutedExpertTensor("blk.3.ffn_gate_inp.weight"));
    }

    [TestMethod]
    public void ActiveParametersAreTheDenseOnesPlusKOfNExperts()
    {
        var a = ModelAnatomyBuilder.Build(Moe(), Args("-m", "x.gguf", "-ngl", "99"));
        Assert.AreEqual(1600 + 25600 * 8 / 128, a.ActiveParameters);
        Assert.AreEqual(Dense().ParameterCount, ModelAnatomyBuilder.Build(Dense(), null).ActiveParameters);
    }

    [TestMethod]
    public void NglCountsFromTheEndAndTheOutputLayerComesFirst()
    {
        var all = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "99"));
        Assert.IsTrue(all.Layers.All(l => l.Main == LayerPlace.Gpu && l.Experts == LayerPlace.Gpu));
        Assert.AreEqual(LayerPlace.Gpu, all.Output);

        var part = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "20"));   // 48 + 1 - 20 = 29: Schichten 29..47 auf der GPU
        Assert.AreEqual(19, part.MainOnGpu);
        Assert.AreEqual(LayerPlace.Cpu, part.Layers[28].Main);
        Assert.AreEqual(LayerPlace.Gpu, part.Layers[29].Main);
        Assert.AreEqual(LayerPlace.Gpu, part.Output);

        var none = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "0"));
        Assert.AreEqual(0, none.MainOnGpu);
        Assert.AreEqual(LayerPlace.Cpu, none.Output);

        Assert.AreEqual(LayerPlace.Gpu, ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "all")).Layers[0].Main);
    }

    [TestMethod]
    public void WithoutNglOrWithAutoThePlacementIsUnknown()
    {
        foreach (var a in new[] { ModelAnatomyBuilder.Build(Moe(), Args("-m", "x")), ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "auto")) })
        {
            Assert.IsFalse(a.PlacementKnown);
            Assert.IsTrue(a.Layers.All(l => l.Main == LayerPlace.Unknown));
            Assert.IsTrue(a.Notes.Contains(Strings.AnatNglAuto));
        }
        Assert.IsTrue(ModelAnatomyBuilder.Build(Moe(), null).Notes.Contains(Strings.AnatNoArgs));
    }

    [TestMethod]
    public void NCpuMoePutsTheExpertsOfTheFirstLayersOnTheCpu()
    {
        var a = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "99", "--n-cpu-moe", "10"));
        Assert.AreEqual(10, a.ExpertsOnCpu);
        Assert.AreEqual(LayerPlace.Cpu, a.Layers[9].Experts);
        Assert.AreEqual(LayerPlace.Gpu, a.Layers[9].Main);     // Attention bleibt auf der GPU
        Assert.AreEqual(LayerPlace.Gpu, a.Layers[10].Experts);
    }

    [TestMethod]
    public void CpuMoePutsAllExpertsOnTheCpu()
    {
        var a = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "99", "-cmoe"));
        Assert.AreEqual(48, a.ExpertsOnCpu);
        Assert.AreEqual(48, a.MainOnGpu);
    }

    [TestMethod]
    public void OverrideTensorRulesAreReadLikeLlamaCpp()
    {
        var a = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "99", "-ot", @"blk\.(1[0-9])\.ffn_.*_exps\.=CPU"));
        CollectionAssert.AreEqual(Enumerable.Range(10, 10).ToArray(), a.Layers.Where(l => l.Experts == LayerPlace.Cpu).Select(l => l.Index).ToArray());

        // mehrere Regeln mit Komma, die erste passende gewinnt; ein GPU-Puffer holt die Schicht zurück
        var b = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "99", "-ot", @"blk\.0\.ffn_.*=CUDA0,\.ffn_.*_exps\.=CPU"));
        Assert.AreEqual(LayerPlace.Gpu, b.Layers[0].Experts);
        Assert.AreEqual(47, b.ExpertsOnCpu);

        var bad = ModelAnatomyBuilder.Build(Moe(), Args("-ngl", "99", "-ot", "blk\\.(=CPU"));
        Assert.AreEqual(0, bad.ExpertsOnCpu);
        Assert.IsTrue(bad.Notes.Any(n => n.StartsWith("-ot pattern", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LeadingDenseLayersHaveNoExperts()
    {
        var a = ModelAnatomyBuilder.Build(Moe(dense: 3), Args("-ngl", "99"));
        Assert.IsFalse(a.Layers[2].Moe);
        Assert.IsTrue(a.Layers[3].Moe);
        Assert.AreEqual(45, a.MoeLayers);
        Assert.AreEqual(0, ModelAnatomyBuilder.Build(Dense(), Args("-ngl", "99")).MoeLayers);
    }

    [TestMethod]
    public void ArgsKnowTheMoeSwitches()
    {
        var a = Args("-ncmoe", "12", "--cpu-moe", "-md", "draft.gguf");
        Assert.AreEqual(12, a.NCpuMoe);
        Assert.IsTrue(a.CpuMoe);
        Assert.AreEqual("draft.gguf", a.ModelDraft);
        Assert.AreEqual(5, LlamaServerArgs.Parse(new[] { "-m", "x" }, new Dictionary<string, string> { ["LLAMA_ARG_N_CPU_MOE"] = "5" }).NCpuMoe);
    }

    [TestMethod]
    public void TheExpertGridLightsExactlyKOfNSpreadOverTheGrid()
    {
        foreach (var (n, k) in new[] { (128, 8), (64, 6), (256, 8), (8, 2), (32, 32), (16, 20) })
        {
            var lit = ModelAnatomyBuilder.LitExperts(n, k);
            Assert.AreEqual(n, lit.Length);
            Assert.AreEqual(Math.Min(n, k), lit.Count(x => x), $"{n}/{k}");
        }
        var cols = ModelAnatomyBuilder.ExpertColumns(128);
        Assert.AreEqual(32, cols);
        var rows = ModelAnatomyBuilder.LitExperts(128, 8).Select((on, i) => (on, i)).Where(x => x.on).Select(x => x.i / cols).Distinct().Count();
        Assert.AreEqual(4, rows);   // in jeder der vier Reihen leuchtet etwas, nicht alles in einer Spalte
        Assert.AreEqual(0, ModelAnatomyBuilder.LitExperts(0, 8).Length);
    }

    [TestMethod]
    public void TheSimulatorReportsWhatItsRequestsAreDoing()
    {
        var w = new SimWorld(seed: 3);
        w.Add(new SimServerSpec { Name = "a", Model = "a", Slots = 1, ThinkPercent = 50, ToolCalls = true, RequestsPerMinute = 600 });
        w.Start();
        var t = DateTime.Now;
        var seen = new HashSet<ProxyLivePhase>();
        for (int i = 0; i < 400; i++)
        {
            t = t.AddMilliseconds(250);
            w.Step(t);
            foreach (var r in w.LiveRequests(t)) { seen.Add(r.Phase); Assert.IsTrue(r.PhaseSince <= t); }
        }
        Assert.IsTrue(seen.Contains(ProxyLivePhase.Thinking) && seen.Contains(ProxyLivePhase.Answering), string.Join(",", seen));
    }

    [TestMethod]
    public void TheSimulatorWritesGgufHeadsWithExperts()
    {
        var spec = new SimServerSpec { Name = "m", Model = "m", Layers = 48, ParamsB = 30.5, Experts = 128, ExpertsUsed = 8, ActiveB = 3.3 };
        var g = Gguf.Read(new MemoryStream(SimGguf.Build(spec)));
        Assert.IsTrue(g.IsMoe);
        Assert.AreEqual(48, g.BlockCount);
        var a = ModelAnatomyBuilder.Build(g, LlamaServerArgs.Parse(new[] { "-ngl", "99", "--n-cpu-moe", "8" }));
        Assert.AreEqual(30.5e9, a.Parameters, 0.2e9);
        Assert.AreEqual(3.3e9, a.ActiveParameters, 0.2e9);
        Assert.AreEqual(8, a.ExpertsOnCpu);
    }

    [TestMethod]
    public void ParamTextIsShort()
    {
        Assert.AreEqual("30.5 B", ModelAnatomyBuilder.ParamText(30_500_000_000));
        Assert.AreEqual("235 B", ModelAnatomyBuilder.ParamText(235_000_000_000));
        Assert.AreEqual("820 M", ModelAnatomyBuilder.ParamText(820_000_000));
    }

    [TestMethod]
    public void SlotPhaseComesFromTheCounters()
    {
        Assert.AreEqual(SlotPhase.Idle, SlotActivity.Of(new SlotView(0, false, false, 0, 0, 0, 8192, 0, 0, -1, 0)).Phase);
        var read = SlotActivity.Of(new SlotView(0, true, true, 0, 0, 4000, 8192, 0, 1200, 0.4, 7, 800));
        Assert.AreEqual(SlotPhase.Reading, read.Phase);
        Assert.AreEqual(0.4, read.ReadFraction, 1e-9);
        var write = SlotActivity.Of(new SlotView(1, true, false, 42, 120, 4120, 8192, 1000, 3200, 1, 7));
        Assert.AreEqual(SlotPhase.Writing, write.Phase);
        Assert.AreEqual(4120.0 / 8192, write.CtxFraction, 1e-9);
        Assert.AreEqual(120.0 / 1000, write.BarFraction, 1e-9);   // beim Schreiben: erzeugt / Grenze
        Assert.AreEqual(0.4, read.BarFraction, 1e-9);
        Assert.AreEqual(0, SlotActivity.Of(new SlotView(0, true, true, 0, 0, 10, 100, 0, 0, -1, 1)).ReadFraction);   // kein Fortschritt gemeldet
    }
}
