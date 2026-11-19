using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N117 – Seitenfehler und eingeblendete Dateien (ProcessPaging, WindowsPlatform): nur der eigene Testprozess und
// temporäre Dateien. Ohne Windows: inconclusive.
[TestClass]
[SupportedOSPlatform("windows")]
public class N117_ProcessPagingTests
{
    private const int Mb = 1 << 20;

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("nur Windows");
    }

    private static string TempFile(string tag) => Path.Combine(Path.GetTempPath(), $"stykker-n117-{tag}-{Guid.NewGuid():N}.bin");

    private static void WriteFile(string path, int bytes)
    {
        var block = new byte[Mb];
        new Random(117).NextBytes(block);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        for (int written = 0; written < bytes; written += block.Length) fs.Write(block, 0, Math.Min(block.Length, bytes - written));
    }

    private static void Touch(MemoryMappedViewAccessor view, long from, long to)
    {
        int page = Environment.SystemPageSize;
        int sum = 0;
        for (long o = from; o < to; o += page) sum += view.ReadByte(o);
        GC.KeepAlive(sum);
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // Die Versätze von SYSTEM_PROCESS_INFORMATION gegen den eigenen Prozess prüfen: Name, Elternprozess, Startzeit, Threads, Working Set
    [TestMethod]
    public void ProcessListOffsetsMatchOwnProcess()
    {
        RequireWindows();
        var list = StykkerLlm.Platform.Windows.ProcessPaging.Processes();
        Assert.IsNotNull(list);
        var me = list.SingleOrDefault(e => e.Pid == Environment.ProcessId);
        Assert.IsNotNull(me, "eigener Prozess nicht in der Liste");
        using var self = Process.GetCurrentProcess();
        Assert.AreEqual(self.ProcessName + ".exe", me.ImageName, ignoreCase: true);
        Assert.AreEqual(self.StartTime.ToFileTime(), me.CreateTime, "CreateTime");
        using var platform = new StykkerLlm.Platform.Windows.WindowsPlatform();
        Assert.AreEqual(platform.ReadProcessBasic(Environment.ProcessId)?.ParentPid, me.ParentPid, "InheritedFromUniqueProcessId");
        Assert.IsTrue(me.Threads > 0 && Math.Abs(me.Threads - self.Threads.Count) <= 10, $"Threads {me.Threads} vs {self.Threads.Count}");
        double ws = Environment.WorkingSet;
        Assert.IsTrue(me.WorkingSet > ws * 0.5 && me.WorkingSet < ws * 2, $"WorkingSet {me.WorkingSet} vs {ws}");
        Assert.IsTrue(me.PageFaults >= me.HardFaults, "PageFaultCount ≥ HardFaultCount");
        Assert.IsTrue(list.Count > 10, "Systemprozesse");
        Console.WriteLine($"{list.Count} processes, own: {me}");
    }

    [TestMethod]
    public void HardFaultsOfOwnProcessDoNotDecrease()
    {
        RequireWindows();
        using IPlatform platform = new StykkerLlm.Platform.Windows.WindowsPlatform();
        var sw = Stopwatch.StartNew();
        var a = platform.ReadHardFaults(Environment.ProcessId);
        var first = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var b = platform.ReadHardFaults(Environment.ProcessId);
        var second = sw.Elapsed.TotalMilliseconds;
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.IsTrue(a >= 0 && b >= a, $"{a} → {b}");
        Assert.IsNull(platform.ReadHardFaults(int.MaxValue - 3));
        var all = StykkerLlm.Platform.Windows.ProcessPaging.AllHardFaults();
        Assert.IsNotNull(all);
        Assert.IsTrue(all.ContainsKey(Environment.ProcessId));
        Console.WriteLine($"hard faults {a} → {b}; first call {first:0.00} ms, second {second:0.00} ms, {all.Count} processes");
    }

    [TestMethod]
    public void ReadsResidencyOfMappedTempFile()
    {
        RequireWindows();
        const int size = 64 * Mb;
        var path = TempFile("map");
        var other = TempFile("other");
        try
        {
            WriteFile(path, size);
            File.WriteAllBytes(other, new byte[4096]);
            using IPlatform platform = new StykkerLlm.Platform.Windows.WindowsPlatform();
            using (var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                Touch(view, 0, size / 2);
                var sw = Stopwatch.StartNew();
                var half = platform.ReadMappedResidency(Environment.ProcessId, path);
                var ms = sw.Elapsed.TotalMilliseconds;
                Assert.IsNotNull(half);
                Assert.IsTrue(half.MappedBytes >= size && half.MappedBytes < size + 2 * Mb, $"MappedBytes {half.MappedBytes}");
                Assert.AreEqual(size / Environment.SystemPageSize, half.SampledPages, "64 MB: jede Seite geprüft");
                Assert.IsTrue((double)half.ResidentPages / half.SampledPages >= 0.4, $"halb berührt: {half}");
                Console.WriteLine($"half: {half} fraction {half.Fraction:0.000} in {ms:0.00} ms");

                Touch(view, 0, size);
                var full = platform.ReadMappedResidency(Environment.ProcessId, path);
                Assert.IsNotNull(full);
                Assert.IsTrue(full.Fraction >= 0.8, $"alles berührt: {full}");

                // andere Schreibweise: Großbuchstaben, Schrägstriche, Anführungszeichen
                var odd = "\"" + path.ToUpperInvariant().Replace('\\', '/') + "\"";
                var again = platform.ReadMappedResidency(Environment.ProcessId, odd);
                Assert.IsNotNull(again, odd);
                Assert.AreEqual(full.MappedBytes, again.MappedBytes);

                // gestaffelte Stichprobe: höchstens maxSamples Seiten
                var strided = StykkerLlm.Platform.Windows.ProcessPaging.MappedResidency(Environment.ProcessId, path, 1000);
                Assert.IsNotNull(strided);
                Assert.IsTrue(strided.SampledPages <= 1000 && strided.SampledPages >= 900, $"{strided}");
                Assert.IsTrue(strided.Fraction >= 0.8, $"{strided}");

                Assert.IsNull(platform.ReadMappedResidency(Environment.ProcessId, other), "nicht eingeblendet");
                Assert.IsNull(platform.ReadMappedResidency(Environment.ProcessId, path + ".missing"), "gibt es nicht");
                Assert.IsNull(platform.ReadMappedResidency(int.MaxValue - 3, path), "kein Prozess");
                Assert.IsNull(platform.ReadMappedResidency(Environment.ProcessId, ""), "leerer Pfad");
            }
        }
        finally { Delete(path); Delete(other); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr overlapped);

    // Große Datei (sparse, 4 GB): die Stichprobe bleibt bei höchstens 65 536 Seiten (+ eine je Bereich)
    [TestMethod]
    public void LargeMappingIsSampled()
    {
        RequireWindows();
        const long size = 4L << 30;
        var path = TempFile("sparse");
        try
        {
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
                if (!DeviceIoControl(fs.SafeFileHandle, 0x900C4 /* FSCTL_SET_SPARSE */, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    Assert.Inconclusive("FSCTL_SET_SPARSE nicht möglich");
                fs.SetLength(size);
            }
            catch (IOException e) { Assert.Inconclusive("Datei nicht anlegbar: " + e.Message); }

            using IPlatform platform = new StykkerLlm.Platform.Windows.WindowsPlatform();
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            Touch(view, 0, 64 * Mb);
            var sw = Stopwatch.StartNew();
            var r = platform.ReadMappedResidency(Environment.ProcessId, path);
            var ms = sw.Elapsed.TotalMilliseconds;
            Assert.IsNotNull(r);
            Assert.IsTrue(r.MappedBytes >= size, $"{r}");
            Assert.IsTrue(r.SampledPages <= 65536 + 16 && r.SampledPages >= 60000, $"{r}");
            Assert.IsTrue(r.Fraction < 0.5, $"nur 64 MB berührt: {r}");
            Console.WriteLine($"sparse 4 GB: {r} fraction {r.Fraction:0.0000} in {ms:0.00} ms");
        }
        finally { Delete(path); }
    }
}
