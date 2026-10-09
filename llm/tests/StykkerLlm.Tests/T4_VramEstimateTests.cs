using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class T4_VramEstimateTests
{
    private static GgufInfo Info(long size = 4L << 30) => new()
    {
        Architecture = "a", FileSize = size,
        Meta = new Dictionary<string, object>
        {
            ["a.block_count"] = 32L, ["a.embedding_length"] = 4096L,
            ["a.attention.head_count"] = 32L, ["a.attention.head_count_kv"] = 8L,
        },
    };

    [TestMethod]
    public void KvCacheIsMonotonicInContextSize()
    {
        double prev = -1;
        foreach (var ctx in new[] { 512, 4096, 8192, 65536, 262144 })
        {
            var kv = VramEstimate.Estimate(Info(), ctx, "f16", "f16").KvCacheGb;
            Assert.IsTrue(kv > prev, "ctx=" + ctx);
            prev = kv;
        }
    }

    [TestMethod]
    public void CacheTypeFactorsF16AboveQ8AboveQ4()
    {
        var f16 = VramEstimate.Estimate(Info(), 65536, "f16", "f16").KvCacheGb;
        var q8 = VramEstimate.Estimate(Info(), 65536, "q8_0", "q8_0").KvCacheGb;
        var q4 = VramEstimate.Estimate(Info(), 65536, "q4_0", "q4_0").KvCacheGb;
        Assert.IsTrue(f16 > q8, "f16 <= q8_0");
        Assert.IsTrue(q8 > q4, "q8_0 <= q4_0");
        Assert.IsTrue(q4 > 0);
        Assert.IsTrue(VramEstimate.BytesPerElement("f16") > VramEstimate.BytesPerElement("q8_0"));
        Assert.IsTrue(VramEstimate.BytesPerElement("q8_0") > VramEstimate.BytesPerElement("q4_0"));
    }

    [TestMethod]
    public void EstimateIsNeverNegative()
    {
        foreach (var (ctx, ngl) in new[] { (0, (int?)null), (-5, (int?)null), (4096, (int?)0), (4096, (int?)99), (int.MaxValue, (int?)null) })
        {
            var e = VramEstimate.Estimate(Info(), ctx, "q4_0", "q4_0", ngl);
            Assert.IsTrue(e.ModelGb >= 0 && e.KvCacheGb >= 0 && e.ComputeGb >= 0 && e.TotalGb >= 0,
                $"ctx={ctx} ngl={ngl}");
        }
    }

    [TestMethod]
    public void TotalIsSumOfComponents()
    {
        var e = VramEstimate.Estimate(Info(), 8192, "q8_0", "q4_0", ngl: 10);
        Assert.AreEqual(e.ModelGb + e.KvCacheGb + e.ComputeGb, e.TotalGb, 1e-12);
    }

    [TestMethod]
    public void MissingMetadataMeansNoKvCacheAndFullModel()
    {
        var bare = new GgufInfo { Architecture = "a", FileSize = 1L << 30 };
        var e = VramEstimate.Estimate(bare, 4096, "f16", "f16");
        Assert.AreEqual(0.0, e.KvCacheGb, 1e-12);
        Assert.AreEqual(1.0, e.ModelGb, 1e-12);
        Assert.AreEqual(VramEstimate.ComputeBufferGb, e.ComputeGb, 1e-12);
    }

    [TestMethod]
    public void ZeroSizeModelCostsNothing()
    {
        var empty = new GgufInfo { Architecture = "a", FileSize = 0, Meta = Info().Meta };
        Assert.AreEqual(0.0, VramEstimate.Estimate(empty, 4096, "f16", "f16", ngl: 99).ModelGb, 1e-12);
    }

    [TestMethod]
    public void UnknownCacheTypeFallsBackToF16()
    {
        Assert.AreEqual(VramEstimate.Estimate(Info(), 4096, "f16", "f16").KvCacheGb,
                        VramEstimate.Estimate(Info(), 4096, "nonsense", (string?)null).KvCacheGb, 1e-12);
    }

    [TestMethod]
    public void MoreGpuLayersNeverIncreaseModelPart()
    {
        double prev = -1;
        foreach (var ngl in new int?[] { 0, 5, 10, 20, 31, 32, 33, 99, null })
        {
            var m = VramEstimate.Estimate(Info(), 4096, "f16", "f16", ngl).ModelGb;
            Assert.IsTrue(m >= prev, "ngl=" + ngl);
            prev = m;
        }
        Assert.AreEqual(4.0, VramEstimate.Estimate(Info(), 4096, "f16", "f16", null).ModelGb, 1e-9);
        Assert.AreEqual(4.0, VramEstimate.Estimate(Info(), 4096, "f16", "f16", 99).ModelGb, 1e-9);
        Assert.AreEqual(0.0, VramEstimate.Estimate(Info(), 4096, "f16", "f16", 0).ModelGb, 1e-9);
    }

    [TestMethod]
    public void CpuRamIsZeroWhenEverythingIsOnTheGpu()
    {
        Assert.AreEqual(0.0, VramEstimate.CpuRamGb(Info(), 4096, "f16", "f16", ngl: 99), 1e-12);
        Assert.AreEqual(0.0, VramEstimate.CpuRamGb(Info(), 4096, "f16", "f16", ngl: null), 1e-12);
    }

    [TestMethod]
    public void CpuRamGrowsAsMoreLayersStayOnTheCpu()
    {
        double prev = -1;
        foreach (var ngl in new int?[] { 32, 20, 10, 0 })
        {
            var ram = VramEstimate.CpuRamGb(Info(), 4096, "f16", "f16", ngl);
            Assert.IsTrue(ram > prev, "ngl=" + ngl);
            prev = ram;
        }
        // ngl 0: ganzes Modell (4 GB) plus KV auf der CPU plus Compute-Puffer
        var kv = VramEstimate.Estimate(Info(), 4096, "f16", "f16").KvCacheGb;
        Assert.AreEqual(4.0 + kv + VramEstimate.ComputeBufferGb, VramEstimate.CpuRamGb(Info(), 4096, "f16", "f16", ngl: 0), 1e-9);
    }
}