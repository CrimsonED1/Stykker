using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Modelle auf der Platte finden: der Ordnerbaum wird nach .gguf abgesucht und nur der Kopf gelesen.
[TestClass]
public class GgufScanTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slm-scan-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string Write(string relative, byte[] bytes)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return full;
        }
        public string Dir(string relative)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(full);
            return full;
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static byte[] Model(string name, uint quant = 15)
    {
        // Ein Tensor, damit die Parameterzahl (aus den Tensor-Dimensionen berechnet) nicht 0 ist
        var bytes = new GgufBuilder()
            .Text("general.architecture", "qwen35")
            .Text("general.name", name)
            .U32("general.file_type", quant)
            .U32("qwen35.block_count", 24)
            .U64("qwen35.context_length", 32768)
            .Tensor("token_embd.weight", new long[] { 1024, 481_000 }, 0)
            .Build();
        // Der Kopf nennt keine Dateigröße; die Testdatei soll groß genug aussehen, damit die Anzeige stimmt
        var padded = new byte[Math.Max(bytes.Length, 1024 * 64)];
        bytes.CopyTo(padded, 0);
        padded[^1] = 0;
        // Die letzten vier Bytes sind der Tensor-Versatz – nach dem Padding wieder auf 0 setzen ist nicht nötig,
        // weil TryRead nur Kopf und Metadaten liest.
        return padded;
    }

    [TestMethod]
    public void Find_ReadsModelsFromNestedFoldersAndSkipsTheRest()
    {
        using var tmp = new TempDir();
        var a = tmp.Write("qwen3-8b-q4_k_m.gguf", Model("Qwen3 8B"));
        tmp.Write("unter/ordner/gemma-3-12b-it-q4_0.gguf", Model("Gemma 3 12B", 2));
        tmp.Write("unter/mmproj-model-f16.gguf", Model("mmproj"));                 // Projektion, kein Modell
        tmp.Write("unter/ordner/mein-lora-adapter.gguf", Model("LoRA"));         // Adapter, kein Modell
        tmp.Write("unter/kein-gguf.txt", System.Text.Encoding.UTF8.GetBytes("x"));  // falsche Endung
        tmp.Write("kaputt/truncated.gguf", new byte[] { 1, 2, 3 });               // kein gültiger Kopf
        tmp.Dir(".git");
        tmp.Write(".git/versteckt.gguf", Model("versteckt"));

        var found = GgufScan.Find(new[] { tmp.Path });

        CollectionAssert.AreEqual(new[] { a, Path.Combine(tmp.Path, "unter", "ordner", "gemma-3-12b-it-q4_0.gguf") },
            found.Select(m => m.Path).ToList(), "nur echte Modelle, nach Pfad sortiert");
        Assert.AreEqual("Qwen3 8B", found[0].Name);
        Assert.AreEqual("Q4_K_M", found[0].Quantization);
        Assert.IsTrue(found[0].ParameterCount > 0, "Parameterzahl aus den Tensor-Dimensionen");
        Assert.AreEqual(32768L, found[0].ContextLength);
    }

    [TestMethod]
    public void Find_ToleratesBadRootsAndRespectsTheLimit()
    {
        using var tmp = new TempDir();
        for (int i = 0; i < 5; i++) tmp.Write($"m{i}.gguf", Model($"Modell {i}"));

        Assert.IsNotNull(GgufScan.Find(new[] { "gibt-es-nicht", "", tmp.Path }));
        Assert.AreEqual(5, GgufScan.Find(new[] { tmp.Path }).Count);

        var limited = GgufScan.Find(new[] { tmp.Path }, limit: 2);
        Assert.AreEqual(2, limited.Count, "Grenze hält");
        Assert.AreEqual(0, GgufScan.Find(Array.Empty<string>()).Count);
        Assert.AreEqual(0, GgufScan.Find(null).Count);
    }

    [TestMethod]
    public void Find_UsesTheSameFileOnlyOnce_WhenRootsOverlap()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.gguf", Model("A"));
        var sub = tmp.Dir("b");
        tmp.Write("b/b.gguf", Model("B"));

        var found = GgufScan.Find(new[] { tmp.Path, sub, file });
        Assert.AreEqual(2, found.Count, "keine Doppelzählung bei überlappenden Ordnern");
    }
}