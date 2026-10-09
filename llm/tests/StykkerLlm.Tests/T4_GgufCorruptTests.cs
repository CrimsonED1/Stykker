using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class T4_GgufCorruptTests
{
    private static byte[] Raw(Action<BinaryWriter> write, int kvCount = 0, int tensorCount = 0, uint version = 3)
    {
        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, true);
        w.Write(0x46554747u); w.Write(version); w.Write((ulong)tensorCount); w.Write((ulong)kvCount);
        write(w);
        w.Flush();
        return ms.ToArray();
    }

    private static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }

    [TestMethod]
    public void RejectsVersionZeroAndHugeVersions()
    {
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(Raw(_ => { }, version: 0))));
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(Raw(_ => { }, version: 0x10000))));
    }

    [TestMethod]
    public void RejectsImplausibleCounts()
    {
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(Raw(_ => { }, kvCount: 10_000_001))));
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(Raw(_ => { }, tensorCount: 10_000_001))));
    }

    [TestMethod]
    public void RejectsUnknownValueType()
    {
        var bytes = Raw(w => { Str(w, "x"); w.Write(20u); }, kvCount: 1);
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(bytes)));
    }

    [TestMethod]
    public void RejectsUnknownArrayElementType()
    {
        var bytes = Raw(w => { Str(w, "x"); w.Write(9u); w.Write(20u); w.Write(1UL); }, kvCount: 1);
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(bytes)));
    }

    [TestMethod]
    public void NestedArraysAreSkippedAndParsingContinues()
    {
        var bytes = Raw(w =>
        {
            Str(w, "nested"); w.Write(9u); w.Write(9u); w.Write(1UL);   // ein Element: Array der Länge 0
            w.Write(9u); w.Write(0UL);
            Str(w, "general.architecture"); w.Write(8u); Str(w, "llama");
        }, kvCount: 2);
        var g = Gguf.Read(new MemoryStream(bytes));
        Assert.AreEqual("llama", g.Architecture);
        Assert.IsFalse(g.Meta.ContainsKey("nested"));
    }

    [TestMethod]
    public void RejectsBadTensorRank()
    {
        var bytes = Raw(w => { Str(w, "t"); w.Write(9u); }, tensorCount: 1);
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(bytes)));
    }

    [TestMethod]
    public void RejectsKeyLongerThan4096()
    {
        var bytes = Raw(w => { w.Write(5000UL); w.Write(new byte[100]); }, kvCount: 1);
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(bytes)));
    }

    [TestMethod]
    public void LongValuesAreStoredAsEmptyAndParsingContinues()
    {
        var bytes = Raw(w =>
        {
            Str(w, "general.name"); w.Write(8u); Str(w, new string('x', 600));
            Str(w, "general.architecture"); w.Write(8u); Str(w, "llama");
        }, kvCount: 2);
        var g = Gguf.Read(new MemoryStream(bytes));
        Assert.AreEqual("", g.Str("general.name"));
        Assert.AreEqual("llama", g.Architecture);
    }

    [TestMethod]
    public void BoolFloatAndI64ArraysAreHandled()
    {
        var bytes = Raw(w =>
        {
            Str(w, "arr_bool"); w.Write(9u); w.Write(7u); w.Write(3UL); w.Write(new byte[3]);
            Str(w, "arr_f32"); w.Write(9u); w.Write(6u); w.Write(2UL); w.Write(new byte[8]);
            Str(w, "arr_i64"); w.Write(9u); w.Write(10u); w.Write(2UL); w.Write(5L); w.Write(6L);
            Str(w, "general.architecture"); w.Write(8u); Str(w, "llama");
        }, kvCount: 4);
        var g = Gguf.Read(new MemoryStream(bytes));
        Assert.IsFalse(g.Meta.ContainsKey("arr_bool"));
        Assert.IsFalse(g.Meta.ContainsKey("arr_f32"));
        CollectionAssert.AreEqual(new long[] { 5, 6 }, (long[])g.Meta["arr_i64"]);
        Assert.AreEqual("llama", g.Architecture);
        Assert.AreEqual(5L, g.Int("arr_i64"));   // Int liefert das erste Element
    }

    [TestMethod]
    public void TruncatedFilesAlwaysFailWithGgufException()
    {
        var bytes = new GgufBuilder()
            .Text("general.architecture", "llama")
            .U32("llama.block_count", 4)
            .IntArray("llama.attention.head_count_kv", new[] { 2, 2 })
            .Tensor("t", new long[] { 4 }, 0)
            .Build();
        for (int cut = 0; cut < bytes.Length; cut += 7)
            Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(bytes, 0, cut)), "cut=" + cut);
        Assert.ThrowsException<GgufException>(() => Gguf.Read(new MemoryStream(bytes, 0, bytes.Length - 1)));
        Gguf.Read(new MemoryStream(bytes));   // vollständig: kein Fehler
    }

    [TestMethod]
    public void TryReadReturnsNullOnCorruptFilesWithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-t4-gguf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = new GgufBuilder().Text("general.architecture", "llama").Tensor("t", new long[] { 4 }, 0).Build();
            var files = new Dictionary<string, byte[]>
            {
                ["garbage.gguf"] = Encoding.ASCII.GetBytes("not a gguf file at all"),
                ["truncated.gguf"] = good[..^5],
                ["empty.gguf"] = Array.Empty<byte>(),
                ["lies-about-kv.gguf"] = Raw(_ => { }, kvCount: 1),
            };
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllBytes(path, content);
                Assert.IsNull(Gguf.TryRead(path), name);
                Assert.IsNull(Gguf.TryRead(path), name + " (zweiter Versuch)");
            }
            Assert.IsNull(Gguf.TryRead(dir));   // Ordner statt Datei
            Assert.IsNull(Gguf.TryRead(""));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void IntHandlesDoubleAndArrayValues()
    {
        var bytes = Raw(w =>
        {
            Str(w, "a.temp"); w.Write(6u); w.Write(2.9f);
            Str(w, "a.head_count_kv"); w.Write(9u); w.Write(5u); w.Write(2UL); w.Write(7); w.Write(8);
        }, kvCount: 2);
        var g = Gguf.Read(new MemoryStream(bytes));
        Assert.AreEqual(2L, g.Int("a.temp"));
        Assert.AreEqual(7L, g.Int("a.head_count_kv"));
        Assert.IsNull(g.Str("a.temp"));
    }
}