using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Baut kleine GGUF-Dateien im Speicher
internal sealed class GgufBuilder
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _w;
    private readonly List<Action> _kv = new();
    private readonly List<(string Name, long[] Dims, uint Type)> _tensors = new();

    public GgufBuilder() => _w = new BinaryWriter(_ms, Encoding.UTF8, true);

    private void Str(string s) { var b = Encoding.UTF8.GetBytes(s); _w.Write((ulong)b.Length); _w.Write(b); }

    public GgufBuilder U32(string key, uint v) { _kv.Add(() => { Str(key); _w.Write(4u); _w.Write(v); }); return this; }
    public GgufBuilder U64(string key, ulong v) { _kv.Add(() => { Str(key); _w.Write(10u); _w.Write(v); }); return this; }
    public GgufBuilder F32(string key, float v) { _kv.Add(() => { Str(key); _w.Write(6u); _w.Write(v); }); return this; }
    public GgufBuilder Bool(string key, bool v) { _kv.Add(() => { Str(key); _w.Write(7u); _w.Write((byte)(v ? 1 : 0)); }); return this; }
    public GgufBuilder Text(string key, string v) { _kv.Add(() => { Str(key); _w.Write(8u); Str(v); }); return this; }
    public GgufBuilder IntArray(string key, int[] v) { _kv.Add(() => { Str(key); _w.Write(9u); _w.Write(5u); _w.Write((ulong)v.Length); foreach (var x in v) _w.Write(x); }); return this; }
    public GgufBuilder StringArray(string key, string[] v) { _kv.Add(() => { Str(key); _w.Write(9u); _w.Write(8u); _w.Write((ulong)v.Length); foreach (var x in v) Str(x); }); return this; }
    public GgufBuilder Tensor(string name, long[] dims, uint type) { _tensors.Add((name, dims, type)); return this; }

    public byte[] Build()
    {
        _ms.SetLength(0);
        _w.Write(0x46554747u); _w.Write(3u); _w.Write((ulong)_tensors.Count); _w.Write((ulong)_kv.Count);
        foreach (var k in _kv) k();
        ulong off = 0;
        foreach (var (name, dims, type) in _tensors)
        {
            Str(name); _w.Write((uint)dims.Length); foreach (var d in dims) _w.Write((ulong)d); _w.Write(type); _w.Write(off); off += 1024;
        }
        return _ms.ToArray();
    }
}

[TestClass]
public class GgufTests
{
    [TestMethod]
    public void ReadsMetadataTensorsAndSkipsArrays()
    {
        var bytes = new GgufBuilder()
            .Text("general.architecture", "qwen35")
            .Text("general.name", "Test Model")
            .Text("general.size_label", "27B")
            .U32("general.file_type", 15)
            .StringArray("tokenizer.ggml.tokens", Enumerable.Range(0, 5000).Select(i => "tok" + i).ToArray())
            .IntArray("tokenizer.ggml.token_type", new int[3000])
            .U32("qwen35.block_count", 64)
            .U32("qwen35.embedding_length", 5120)
            .U32("qwen35.attention.head_count", 24)
            .U32("qwen35.attention.head_count_kv", 4)
            .U32("qwen35.attention.key_length", 256)
            .U32("qwen35.full_attention_interval", 4)
            .U64("qwen35.context_length", 262144)
            .F32("qwen35.rope.freq_base", 1e6f)
            .Bool("qwen35.flag", true)
            .Tensor("a", new long[] { 100, 10 }, 12)
            .Tensor("b", new long[] { 50 }, 0)
            .Build();
        var g = Gguf.Read(new MemoryStream(bytes), "x.gguf", 12345);
        Assert.AreEqual(3, g.Version);
        Assert.AreEqual("qwen35", g.Architecture);
        Assert.AreEqual("Test Model", g.Name);
        Assert.AreEqual("27B", g.SizeLabel);
        Assert.AreEqual("Q4_K_M", g.Quantization);
        Assert.AreEqual(1050, g.ParameterCount);
        Assert.AreEqual(2, g.TensorCount);
        Assert.AreEqual(64, g.BlockCount);
        Assert.AreEqual(5120, g.EmbeddingLength);
        Assert.AreEqual(24, g.HeadCount);
        Assert.AreEqual(256, g.KeyLength);
        Assert.AreEqual(4, g.FullAttentionInterval);
        Assert.AreEqual(262144L, g.ContextLength);
        Assert.IsFalse(g.Meta.ContainsKey("tokenizer.ggml.tokens"));
        Assert.AreEqual(12345, g.FileSize);
    }

    [TestMethod]
    public void QuantizationFallsBackToDominantTensorType()
    {
        var bytes = new GgufBuilder().Text("general.architecture", "llama")
            .Tensor("big", new long[] { 1000, 1000 }, 12).Tensor("small", new long[] { 10 }, 0).Build();
        Assert.AreEqual("Q4_K", Gguf.Read(new MemoryStream(bytes)).Quantization);
    }

    [TestMethod]
    public void HeadCountKvPerLayerList()
    {
        var bytes = new GgufBuilder().Text("general.architecture", "x").U32("x.block_count", 4)
            .IntArray("x.attention.head_count_kv", new[] { 0, 8, 0, 8 }).Build();
        var g = Gguf.Read(new MemoryStream(bytes));
        CollectionAssert.AreEqual(new long[] { 0, 8, 0, 8 }, g.HeadCountKvPerLayer);
    }

    [TestMethod]
    public void RejectsGarbageAndTruncatedFiles()
    {
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(Encoding.ASCII.GetBytes("not a gguf file at all"))));
        var bytes = new GgufBuilder().Text("general.architecture", "llama").Tensor("t", new long[] { 4 }, 0).Build();
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(bytes, 0, bytes.Length - 10)));
    }

    [TestMethod]
    public void TryReadUsesCacheAndSurvivesMissingFile()
    {
        Assert.IsNull(Gguf.TryRead(null));
        Assert.IsNull(Gguf.TryRead(Path.Combine(Path.GetTempPath(), "does-not-exist.gguf")));
        var path = Path.Combine(Path.GetTempPath(), "slm-test-" + Guid.NewGuid().ToString("N") + ".gguf");
        try
        {
            File.WriteAllBytes(path, new GgufBuilder().Text("general.architecture", "llama").Text("general.name", "A").Tensor("t", new long[] { 8 }, 0).Build());
            var a = Gguf.TryRead(path);
            Assert.IsNotNull(a);
            Assert.AreSame(a, Gguf.TryRead(path));   // aus dem Cache
            File.WriteAllBytes(path, new GgufBuilder().Text("general.architecture", "llama").Text("general.name", "BB").Tensor("t", new long[] { 8 }, 0).Build());
            Assert.AreEqual("BB", Gguf.TryRead(path)!.Name);   // Größe geändert: neu gelesen
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void RealFileIfProvided()
    {
        var path = Environment.GetEnvironmentVariable("SLM_TEST_GGUF");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;   // optional: eigene Modelldatei per Umgebungsvariable
        var g = Gguf.TryRead(path);
        Assert.IsNotNull(g);
        Assert.IsTrue(g.ParameterCount > 1_000_000);
        Assert.IsFalse(string.IsNullOrEmpty(g.Architecture));
    }
}

[TestClass]
public class VramEstimateTests
{
    private static GgufInfo Info(Dictionary<string, object> meta, long size = 4L << 30) => new()
    {
        Architecture = "a", FileSize = size, Meta = meta,
    };

    [TestMethod]
    public void KvCacheOnlyForFullAttentionLayers()
    {
        var meta = new Dictionary<string, object>
        {
            ["a.block_count"] = 64L, ["a.embedding_length"] = 5120L, ["a.attention.head_count"] = 24L,
            ["a.attention.head_count_kv"] = 4L, ["a.attention.key_length"] = 256L, ["a.full_attention_interval"] = 4L,
        };
        var e = VramEstimate.Estimate(Info(meta), 65536, "q4_0", "q4_0");
        // 16 volle Schichten * 4 KV-Köpfe * 256 * 2 (K+V) * 0.5625 Byte * 65536 Token
        double expected = 16.0 * 4 * 256 * 2 * (18.0 / 32) * 65536 / (1024.0 * 1024 * 1024);
        Assert.AreEqual(expected, e.KvCacheGb, 1e-6);
        Assert.AreEqual(4.0, e.ModelGb, 1e-6);
        Assert.AreEqual(e.ModelGb + e.KvCacheGb + VramEstimate.ComputeBufferGb, e.TotalGb, 1e-9);
    }

    [TestMethod]
    public void DenseModelUsesAllLayersAndF16()
    {
        var meta = new Dictionary<string, object>
        {
            ["a.block_count"] = 32L, ["a.embedding_length"] = 4096L, ["a.attention.head_count"] = 32L, ["a.attention.head_count_kv"] = 8L,
        };
        var e = VramEstimate.Estimate(Info(meta), 8192, null, null);
        double expected = 32.0 * 8 * 128 * 2 * 2 * 8192 / (1024.0 * 1024 * 1024);
        Assert.AreEqual(expected, e.KvCacheGb, 1e-6);
    }

    [TestMethod]
    public void PerLayerListAndPartialOffload()
    {
        var meta = new Dictionary<string, object>
        {
            ["a.block_count"] = 4L, ["a.attention.head_count"] = 8L, ["a.embedding_length"] = 512L,
            ["a.attention.head_count_kv"] = new long[] { 0, 2, 0, 2 },
        };
        var e = VramEstimate.Estimate(Info(meta, 5L << 30), 1000, "f16", "f16", ngl: 2);
        Assert.AreEqual(5.0 * 2 / 5, e.ModelGb, 1e-6);   // 2 von 5 Schichten (4 + Ausgabe)
        double kv = 1000 * 4.0 * (64 * 2 + 64 * 2) / (1024.0 * 1024 * 1024);
        Assert.AreEqual(kv, e.KvCacheGb, 1e-9);
    }

    [TestMethod]
    public void BytesPerElementKnownTypes()
    {
        Assert.AreEqual(2.0, VramEstimate.BytesPerElement("f16"));
        Assert.AreEqual(34.0 / 32, VramEstimate.BytesPerElement("q8_0"));
        Assert.AreEqual(2.0, VramEstimate.BytesPerElement("unknown"));
    }
}

[TestClass]
public class GgufNamesTests
{
    [TestMethod]
    public void BonsaiFormatsAreNamed()
    {
        Assert.AreEqual("Q1_0", Gguf.FileTypeName(40));
        Assert.AreEqual("PQ2_0", Gguf.FileTypeName(141));
        Assert.AreEqual("Q1_0", Gguf.TensorTypeName(41));
        Assert.AreEqual("type 999", Gguf.TensorTypeName(999));
        Assert.IsNull(Gguf.FileTypeName(4242));
    }
}
