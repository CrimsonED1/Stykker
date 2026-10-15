using System.Diagnostics;
using Stykker.Shared.Gpu;
using Stykker.Shared.Windows;
using StykkerSys.Core;

namespace StykkerSys.Tests;

// Die Übersicht: die Aufbereitung der Werte (ohne Windows) und die Messung mit einer festen Sonde
public class OverviewTests
{
    // Eine Sonde mit festen Zahlen; sie zählt, wie oft der Grafikspeicher gefragt wird. Answer = false: nichts liefert sie.
    private sealed class FakeHardwareProbe : IHardwareProbe
    {
        public int MemoryReads;
        public bool Answer = true;

        public SystemLoad? ReadSystem() => Answer
            ? new SystemLoad(37.5, 4, new[] { 10.0, 20, 30, 40 }, 12, 32, 20, 40)
            : null;

        public GpuUtilSample? ReadGpuUtil() => Answer
            ? new GpuUtilSample(new[] { new GpuProcRow(Environment.ProcessId, 30) },
                new[] { new GpuEngineRow("3D", 55), new GpuEngineRow("Copy", 10) })
            : null;

        public IReadOnlyList<GpuProcRow>? ReadGpuMemory()
        {
            MemoryReads++;
            return Answer ? new[] { new GpuProcRow(Environment.ProcessId, 512) } : null;
        }

        public double? ReadGpuMemoryUsedMb() => Answer ? 2048 : null;
        public void Dispose() { }
    }

    [Fact]
    public void GleichnamigeProzesseWerdenEinProgrammMitSummen()
    {
        var programs = OverviewMath.ByName(new[]
        {
            new ProcessMeasure(100, "chrome", 300, 1.5),
            new ProcessMeasure(101, "Chrome", 200, 0.5),
            new ProcessMeasure(102, "llama-server", 4000, 20),
        });

        var chrome = programs.Single(p => p.Name == "chrome");
        Assert.Equal(2, chrome.Count);
        Assert.Equal(500, chrome.RamMb);
        Assert.Equal(2.0, chrome.CpuPercent);
        Assert.Equal(2, programs.Count);
    }

    [Fact]
    public void SystemUndSchutzprozesseFallenWeg()
    {
        var programs = OverviewMath.ByName(new[]
        {
            new ProcessMeasure(4, "System", 10, 5),
            new ProcessMeasure(0, "Idle", 10, 5),
            new ProcessMeasure(200, "", 10, 5),
            new ProcessMeasure(201, "notepad", 50, 1),
        });

        Assert.Equal("notepad", Assert.Single(programs).Name);
    }

    [Fact]
    public void OhneMessungBleibtDerProzessorAnteilLeer()
    {
        var program = Assert.Single(OverviewMath.ByName(new[] { new ProcessMeasure(300, "app", 80, null) }));

        Assert.Null(program.CpuPercent);
        Assert.Equal(80, program.RamMb);
        Assert.Empty(OverviewMath.TopByCpu(new[] { program }, 8));
    }

    [Fact]
    public void DieGroesstenProgrammeStehenObenUndSindBegrenzt()
    {
        var programs = new[]
        {
            new ProgramLoad("small", 1, 100, 9),
            new ProgramLoad("big", 1, 900, 1),
            new ProgramLoad("middle", 1, 500, null),
        };

        var byRam = OverviewMath.TopByRam(programs, 2);
        Assert.Equal(new[] { "big", "middle" }, byRam.Select(p => p.Name));

        var byCpu = OverviewMath.TopByCpu(programs, 8);
        Assert.Equal(new[] { "small", "big" }, byCpu.Select(p => p.Name));   // „middle“ hat keine Messung
    }

    [Fact]
    public void DieGpuAuslastungIstDieHoechsteEngineNichtDieSumme()
    {
        var util = new GpuUtilSample(Array.Empty<GpuProcRow>(),
            new[] { new GpuEngineRow("3D", 70), new GpuEngineRow("Copy", 60) });

        Assert.Equal(70.0, OverviewMath.UtilTotal(util));
        Assert.Null(OverviewMath.UtilTotal(null));                       // kein Zähler: keine Zahl
        Assert.Equal(0.0, OverviewMath.UtilTotal(new GpuUtilSample(Array.Empty<GpuProcRow>(), Array.Empty<GpuEngineRow>())));
    }

    [Fact]
    public void DieGpuProgrammeNehmenNurNamentlicheProzesseUndSortierenNachSpeicher()
    {
        var util = new GpuUtilSample(
            new[] { new GpuProcRow(10, 20), new GpuProcRow(10, 5), new GpuProcRow(11, 40), new GpuProcRow(99, 80) },
            Array.Empty<GpuEngineRow>());
        var memory = new[] { new GpuProcRow(11, 1024), new GpuProcRow(12, 256) };
        var names = new Dictionary<int, string> { [10] = "game", [11] = "browser", [12] = "viewer" };

        var rows = OverviewMath.GpuByName(util, memory, names, 8);

        // PID 99 kennt keine Liste (beendet): er fällt weg. „game“ hat keinen Speicherwert.
        Assert.Equal(new[] { "browser", "viewer", "game" }, rows.Select(r => r.Name));
        Assert.Equal(1024, rows[0].VramMb);
        Assert.Equal(40, rows[0].GpuPercent);
        Assert.Null(rows[2].VramMb);
        Assert.Equal(25, rows[2].GpuPercent);
    }

    [Fact]
    public void EineMessungMitFesterSondeHatWerteUndErstenTaktOhneProzessorAnteil()
    {
        var sampler = new OverviewSampler(new FakeHardwareProbe());

        var first = sampler.Sample();
        Assert.Null(first.System!.CpuPercent);                // erster Takt: noch keine zweite Messung
        Assert.Equal(4, first.System.Cores);
        Assert.Equal(12, first.System.RamUsedGb);
        Assert.Equal(55, first.Gpu!.UtilPercent);             // die höchste Engine (3D), nicht die Summe
        Assert.Equal(2048, first.Gpu.VramUsedMb);

        var second = sampler.Sample();
        Assert.Equal(37.5, second.System!.CpuPercent);
    }

    [Fact]
    public void DerGrafikspeicherWirdJedenDrittenTaktGelesen()
    {
        var probe = new FakeHardwareProbe();
        var sampler = new OverviewSampler(probe);
        for (int i = 0; i < 4; i++) sampler.Sample();

        Assert.Equal(2, probe.MemoryReads);                   // Takt 0 und 3
    }

    [Fact]
    public void DasEigeneProgrammErscheintMitGpuWerten()
    {
        var snapshot = new OverviewSampler(new FakeHardwareProbe()).Sample();
        string me = Process.GetCurrentProcess().ProcessName;

        var row = snapshot.Gpu!.Programs.Single(p => p.Name.Equals(me, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(30, row.GpuPercent);
        Assert.Equal(512, row.VramMb);
    }

    [Fact]
    public void FehlendeZaehlerWerdenInDenHinweisenGenannt()
    {
        var snapshot = new OverviewSampler(new FakeHardwareProbe { Answer = false }).Sample();

        Assert.Null(snapshot.System);
        Assert.Null(snapshot.Gpu);
        Assert.Contains(snapshot.Notes, n => n.StartsWith("No processor and memory values", StringComparison.Ordinal));
        Assert.Contains(snapshot.Notes, n => n.StartsWith("No GPU values", StringComparison.Ordinal));
    }

    [Fact]
    public void OhneWindowsBleibtAllesLeerAberDieMessungLaeuft()
    {
        var snapshot = new OverviewSampler(new BasicHardwareProbe()).Sample();

        Assert.Null(snapshot.System);
        Assert.Null(snapshot.Gpu);
        Assert.NotEmpty(snapshot.TopByRam);
    }
}
