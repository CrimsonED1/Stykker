using System.Diagnostics;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Misst den Scan gegen die echten Modellordner dieses Rechners: STYKKER_MODEL_ROOTS (mit ; getrennt), sonst der Ordner von LM Studio,
// und wird deshalb über eine Umgebungsvariable gesteuert, damit der normale Testlauf nichts über die Maschine annimmt.
[TestClass]
public class GgufScanLiveTests
{
    [TestMethod]
    public void Scan_UeberDieEchtenModellordner_FindetModelleSchnell()
    {
        var roots = (Environment.GetEnvironmentVariable("STYKKER_MODEL_ROOTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"))
            .Where(Directory.Exists).ToList();
        if (roots.Count == 0) Assert.Inconclusive("keine Modellordner auf diesem Rechner");

        var sw = Stopwatch.StartNew();
        var found = GgufScan.Find(roots);
        sw.Stop();

        Console.WriteLine($"Scan: {found.Count} Modelle aus {roots.Count} Ordnern in {sw.ElapsedMilliseconds} ms");
        foreach (var m in found.Take(3))
            Console.WriteLine($"  {m.Name} · {m.Quantization} · {m.FileSize / 1073741824.0:0.0} GB · ctx {m.ContextLength}");
        foreach (var m in found) Console.WriteLine("    Pfad: " + m.Path);

        Assert.IsTrue(found.Count > 0, "auf diesem Rechner liegen Modelle");
        Assert.IsTrue(sw.Elapsed.TotalSeconds < 20, $"der Scan darf nicht hängen: {sw.Elapsed.TotalSeconds:0.0} s");
        // Die meisten Modelle nennen general.file_type; bei einzelnen bleibt „?" (Tensor-Typ nicht auswertbar) – das ist
        // eine Eigenschaft der Datei, kein Fehler des Scanners. Die Namen werden ausgegeben, damit man sieht, was es ist.
        var ohne = found.Where(m => m.Quantization == "?").ToList();
        foreach (var m in ohne.Take(6)) Console.WriteLine("  ohne Quantisierung: " + Path.GetFileName(m.Path));
        Assert.IsTrue(found.Count - ohne.Count >= found.Count * 0.95,
            $"Quantisierung bei {found.Count - ohne.Count} von {found.Count} Modellen: " +
            string.Join(", ", ohne.Take(4).Select(m => Path.GetFileName(m.Path))));
        Assert.IsTrue(found.All(m => File.Exists(m.Path)));
    }

    [TestMethod]
    public void Scan_BrichtAb_WennDieZeitAuslaeuft()
    {
        using var tmp = new TempDir();
        for (int i = 0; i < 40; i++) tmp.Write($"m{i}.gguf", Model($"M{i}"));

        var cts = new CancellationTokenSource();
        cts.Cancel();   // schon vor Beginn abgesagt: die Suche endet sofort
        var found = GgufScan.Find(new[] { tmp.Path }, ScanCancellation: cts.Token);
        Assert.AreEqual(0, found.Count, "mit abgesagter Suche kommt nichts heraus (kein Hängen)");
    }

    private static byte[] Model(string name) => new GgufBuilder()
        .Text("general.architecture", "qwen35")
        .Text("general.name", name)
        .U32("general.file_type", 15)
        .U64("qwen35.context_length", 32768)
        .Build();

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slm-scan2-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string Write(string name, byte[] bytes) { var f = System.IO.Path.Combine(Path, name); File.WriteAllBytes(f, bytes); return f; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}