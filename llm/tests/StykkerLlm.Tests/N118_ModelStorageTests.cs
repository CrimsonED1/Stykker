using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N118 – Abschnitt „Storage“ der Ansicht „Model“: Lademodus (-lm und die alten Schalter), Modellpuffer aus dem Ladelog,
// das Nachladen von der Platte über die Zeit (PagingTracker) und das Urteil. Keine echten Prozesse: nur Zahlen.
[TestClass]
public class N118_ModelStorageTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 12, 0, 0);
    private const double Gib = 1073741824.0;

    private static LlamaServerArgs Args(params string[] a) => LlamaServerArgs.Parse(a);

    [TestMethod]
    public void LoadModeComesFromLmOrTheOldSwitches()
    {
        Assert.AreEqual((ModelLoadMode.Mmap, true), ModelLoad.Of(Args("-m", "x")));
        Assert.AreEqual((ModelLoadMode.Mmap, true), ModelLoad.Of(null));
        Assert.AreEqual((ModelLoadMode.Mmap, true), ModelLoad.Of(Args("-lm", "auto")));
        Assert.AreEqual((ModelLoadMode.Mmap, false), ModelLoad.Of(Args("--load-mode", "mmap")));
        Assert.AreEqual((ModelLoadMode.Lock, false), ModelLoad.Of(Args("-lm", "mlock")));
        Assert.AreEqual((ModelLoadMode.MmapLock, false), ModelLoad.Of(Args("-lm", "mmap+mlock")));
        Assert.AreEqual((ModelLoadMode.Read, false), ModelLoad.Of(Args("-lm", "none")));
        Assert.AreEqual((ModelLoadMode.DirectIo, false), ModelLoad.Of(Args("-lm", "dio")));
        Assert.AreEqual((ModelLoadMode.Read, false), ModelLoad.Of(Args("--no-mmap")));
        Assert.AreEqual((ModelLoadMode.Lock, false), ModelLoad.Of(Args("--no-mmap", "--mlock")));
        Assert.AreEqual((ModelLoadMode.MmapLock, false), ModelLoad.Of(Args("--mlock")));
        Assert.AreEqual((ModelLoadMode.Read, false),
            ModelLoad.Of(LlamaServerArgs.Parse(new[] { "-m", "x" }, new Dictionary<string, string> { ["LLAMA_ARG_LOAD_MODE"] = "none" })));
        Assert.IsTrue(ModelLoad.CanPage(ModelLoadMode.Mmap));
        Assert.IsFalse(ModelLoad.CanPage(ModelLoadMode.MmapLock));
    }

    [TestMethod]
    public void BuffersAreReadFromTheLoadLog()
    {
        const string log = "llama_model_loader: loaded meta data\n" +
                           "load_tensors: loading model tensors, this can take a while... (mmap = true)\n" +
                           "load_tensors:        CUDA0 model buffer size =  9000.00 MiB\n" +
                           "load_tensors: loading model tensors, this can take a while... (mmap = true)\n" +   // neu geladen: nur das zählt
                           "load_tensors: offloaded 49/49 layers to GPU\n" +
                           "load_tensors:        CUDA0 model buffer size = 14920.50 MiB\n" +
                           "load_tensors:   CPU_Mapped model buffer size =  2790.25 MiB\n" +
                           "load_tensors:          CPU model buffer size =   300.00 MiB\n" +
                           "srv  update_slots: all slots are idle\n";
        var b = ModelBuffers.Parse(log);
        Assert.AreEqual(3, b.Count);
        Assert.AreEqual(14920.5, b.Single(x => x.Name == "CUDA0").MiB, 1e-9);
        var mapped = b.Single(x => x.Mapped);
        Assert.AreEqual("CPU_Mapped", mapped.Name);
        Assert.IsTrue(mapped.Host);
        Assert.IsFalse(b.Single(x => x.Name == "CUDA0").Host);
        Assert.IsTrue(new ModelBuffer("CUDA_Host", 1).Host);
        Assert.AreEqual(0, ModelBuffers.Parse("nothing here").Count);
    }

    [TestMethod]
    public void RepeatedBufferLinesOfNewerBuildsAreSummed()
    {
        // so schreibt b11854 mit -lv 4 (CPU-Build, Qwen3-VL 4B): je Tensorgruppe eine Zeile, mit Zeitstempel davor
        const string log = "0.00.704.035 I load_tensors: loading model tensors, this can take a while... (load_mode = mmap)\n" +
                           "0.00.966.478 I load_tensors:   CPU_Mapped model buffer size =   304.29 MiB\n" +
                           "0.00.966.685 I load_tensors:   CPU_Mapped model buffer size =     0.01 MiB\n" +
                           "0.00.966.709 I load_tensors:   CPU_Mapped model buffer size =    21.53 MiB\n" +
                           "0.00.966.858 I load_tensors:   CPU_REPACK model buffer size =  1683.28 MiB\n";
        var b = ModelBuffers.Parse(log);
        Assert.AreEqual(2, b.Count);
        Assert.AreEqual(325.83, b.Single(x => x.Mapped).MiB, 1e-6);
        var repack = b.Single(x => x.Name == "CPU_REPACK");
        Assert.IsTrue(repack.Host);
        Assert.IsFalse(repack.Mapped);
    }

    [TestMethod]
    public void TheTrackerSeesPagingFromTheHardFaults()
    {
        var t = new PagingTracker();
        t.Sample(T0, 42, 1000, null, false);
        Assert.IsNull(t.HardFaultsPerSec);                        // erste Lesung: nur die Basis
        t.Sample(T0.AddSeconds(3), 42, 1000 + 3 * 10, null, false);
        Assert.AreEqual(10, t.HardFaultsPerSec!.Value, 1e-9);
        Assert.IsFalse(t.PagingNow(T0.AddSeconds(3)));            // 10/s: kein Nachladen
        t.Sample(T0.AddSeconds(6), 42, 1030 + 3 * 2000, null, false);
        Assert.IsTrue(t.PagingNow(T0.AddSeconds(6)));
        Assert.IsTrue(t.PagingNow(T0.AddSeconds(6) + PagingTracker.Hold));
        Assert.IsFalse(t.PagingNow(T0.AddSeconds(7) + PagingTracker.Hold));
    }

    [TestMethod]
    public void ReadingCountsOnlyWhilePaging()
    {
        var t = new PagingTracker();
        t.Tick(T0, 500e6);
        t.Tick(T0.AddSeconds(1), 500e6);
        Assert.AreEqual(0, t.ReadBytes);                          // liest jemand, aber der Server lädt nicht nach
        t.Sample(T0.AddSeconds(1), 7, 0, null, false);
        t.Sample(T0.AddSeconds(2), 7, 5000, null, false);         // 5000/s
        t.Tick(T0.AddSeconds(2), 400e6);
        t.Tick(T0.AddSeconds(3), 400e6);
        Assert.AreEqual(800e6, t.ReadBytes, 1);                   // zwei Takte zu je einer Sekunde
        Assert.AreEqual(400e6, t.ReadBytesPerSec, 1);
        t.Tick(T0.AddSeconds(60), 400e6);                         // lange Pause: nicht mehr nachladend, nichts kommt dazu
        Assert.AreEqual(800e6, t.ReadBytes, 1);
        Assert.AreEqual(0, t.ReadBytesPerSec);
    }

    [TestMethod]
    public void ANewProcessStartsTheCountOver()
    {
        var t = new PagingTracker();
        t.Sample(T0, 1, 0, new MappedFileResidency(10, 5, 2, 1), true);
        t.Sample(T0.AddSeconds(1), 1, 9000, null, false);
        t.Tick(T0.AddSeconds(1), 1e6); t.Tick(T0.AddSeconds(2), 1e6);
        Assert.IsTrue(t.ReadBytes > 0);
        Assert.IsNotNull(t.Residency);                            // ohne neue Lesung bleibt der RAM-Anteil stehen
        t.Sample(T0.AddSeconds(3), 2, 50, null, false);
        Assert.AreEqual(0, t.ReadBytes);
        Assert.IsNull(t.HardFaultsPerSec);
        Assert.IsNull(t.Residency);
    }

    private static ModelStorage Storage(ModelLoadMode mode, MappedFileResidency? res, bool paging, double mappedMib = 2048, double readBytes = 0,
        double readBps = 0, double tps = 0) =>
        new(mode, false, new[] { new ModelBuffer("CUDA0", 15000), new ModelBuffer("CPU_Mapped", mappedMib) }, (long)(17 * Gib), "D", "Nvme",
            res, paging ? 3000 : 2, paging, readBps, readBytes, tps);

    [TestMethod]
    public void TheShareInRamIsMeasuredAgainstTheMappedPart()
    {
        var s = Storage(ModelLoadMode.Mmap, new MappedFileResidency((long)(17 * Gib), (long)(1.5 * Gib), 1000, 88), false);
        Assert.AreEqual(1.5 / 2.0, s.MappedInRam!.Value, 1e-6);
        Assert.AreEqual(15000 / 1024.0, s.DeviceGb, 1e-9);
        Assert.AreEqual(2.0, s.MappedGb, 1e-9);
        // ohne Pufferzeilen: gegen die ganze Datei
        var noBuf = new ModelStorage(ModelLoadMode.Mmap, true, Array.Empty<ModelBuffer>(), 100, null, null,
            new MappedFileResidency(100, 25, 4, 1), null, false, 0, 0, 0);
        Assert.AreEqual(0.25, noBuf.MappedInRam!.Value, 1e-9);
    }

    [TestMethod]
    public void TheVerdictFollowsModeThenMeasurement()
    {
        Assert.AreEqual(StorageVerdict.Locked, Storage(ModelLoadMode.MmapLock, null, true).Verdict);
        Assert.AreEqual(StorageVerdict.Locked, Storage(ModelLoadMode.Lock, null, false).Verdict);
        Assert.AreEqual(StorageVerdict.ReadOnce, Storage(ModelLoadMode.Read, null, true).Verdict);
        Assert.AreEqual(StorageVerdict.ReadOnce, Storage(ModelLoadMode.DirectIo, null, false).Verdict);
        Assert.AreEqual(StorageVerdict.Paging, Storage(ModelLoadMode.Mmap, null, true).Verdict);
        var full = new MappedFileResidency((long)(17 * Gib), (long)(2 * Gib), 10, 10);
        Assert.AreEqual(StorageVerdict.InMemory, Storage(ModelLoadMode.Mmap, full, false).Verdict);
        var part = new MappedFileResidency((long)(17 * Gib), (long)(1 * Gib), 10, 5);
        Assert.AreEqual(StorageVerdict.Unknown, Storage(ModelLoadMode.Mmap, part, false).Verdict);
        // fast alles auf der GPU (eingeblendet bleibt weniger als 1 GB): im Speicher, egal wie viel davon im RAM liegt
        Assert.AreEqual(StorageVerdict.InMemory, Storage(ModelLoadMode.Mmap, part, false, mappedMib: 300).Verdict);
        Assert.AreEqual(StorageVerdict.Unknown, Storage(ModelLoadMode.Mmap, null, false).Verdict);
    }

    [TestMethod]
    public void ReadTimesAndBytesPerToken()
    {
        var s = Storage(ModelLoadMode.Mmap, null, true, readBytes: 34 * Gib, readBps: 600e6, tps: 12);
        Assert.AreEqual(2.0, s.ReadTimes!.Value, 1e-6);
        Assert.AreEqual(50e6, s.BytesPerToken!.Value, 1);
        Assert.IsNull(Storage(ModelLoadMode.Mmap, null, true, readBps: 600e6, tps: 0).BytesPerToken);
        Assert.IsNull(Storage(ModelLoadMode.Mmap, null, false).ReadTimes);
    }

    [TestMethod]
    public void DriveLetterOfTheModelFile()
    {
        Assert.AreEqual("D", ModelStorage.DriveOf(@"d:\models\x.gguf"));
        Assert.IsNull(ModelStorage.DriveOf(@"\\nas\models\x.gguf"));
        Assert.IsNull(ModelStorage.DriveOf("/home/me/x.gguf"));
        Assert.IsNull(ModelStorage.DriveOf(null));
    }

    [TestMethod]
    public void TheSimulatorLogsItsBuffersAndPagesOnlyTheMoeModel()
    {
        var moe = SimServerSpec.Defaults().Single(s => s.Experts > 0);
        var buffers = ModelBuffers.Parse(SimGguf.LoadLog(moe));
        double mapped = buffers.Where(b => b.Mapped).Sum(b => b.MiB), total = buffers.Sum(b => b.MiB);
        Assert.AreEqual(moe.ModelGb * 1024, total, 1);
        Assert.AreEqual(SimGguf.CpuShare(moe), mapped / total, 0.01);
        Assert.IsTrue(SimGguf.CpuShare(moe) is > 0.1 and < 0.25);   // die Experten von 8 der 48 Schichten
        Assert.AreEqual(0.02, SimGguf.CpuShare(SimServerSpec.Defaults()[0]), 1e-9);
    }
}
