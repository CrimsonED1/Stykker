using Stykker.Shared.Hardware;

namespace Stykker.Shared.Tests;

// Die Umrechnung der Prozessor- und Speicherwerte mit Testdaten – keine echten Zähler vorausgesetzt.
public class SystemMathTests
{
    [Fact]
    public void CpuPercentIsTheBusyShareOfTheElapsedTime()
    {
        // 100 Einheiten vergangen (Kernel 60 inkl. Leerlauf 30, User 40): davon 30 Leerlauf, also 70 % belegt
        Assert.Equal(70.0, SystemMath.CpuPercent(idle: 130, kernel: 160, user: 140, prevIdle: 100, prevKernel: 100, prevUser: 100));
    }

    [Fact]
    public void CpuPercentWithoutElapsedTimeIsUnknown()
    {
        Assert.Null(SystemMath.CpuPercent(10, 20, 30, 10, 20, 30));
    }

    [Fact]
    public void CpuPercentIsClampedToTheRange()
    {
        // Ein Leerlauf-Zähler, der weniger zählt als die Gesamtzeit, darf nicht über 100 % oder unter 0 % führen
        Assert.Equal(100.0, SystemMath.CpuPercent(idle: 100, kernel: 200, user: 100, prevIdle: 100, prevKernel: 100, prevUser: 100));
        Assert.Equal(0.0, SystemMath.CpuPercent(idle: 300, kernel: 200, user: 100, prevIdle: 100, prevKernel: 100, prevUser: 100));
    }

    [Theory]
    [InlineData(100, 25, 75.0)]
    [InlineData(100, 100, 0.0)]
    [InlineData(0, 0, 0.0)]     // ohne Zeit im Kern: 0 %, nicht durch null teilen
    public void CoreLoadIsBusyOverTotal(long total, long idle, double expected) =>
        Assert.Equal(expected, SystemMath.CoreLoad(total, idle));

    [Fact]
    public void TopProgramsSumsSameNamesAndLeavesOutTheKernel()
    {
        const long gib = 1L << 30;
        var rows = new (int Pid, string Name, long WorkingSetBytes)[]
        {
            (4, "System", 5 * gib),          // PID 4 und darunter: weg
            (120, "llama-server", 2 * gib),
            (121, "llama-server", 3 * gib),  // gleichnamig: zusammen 5 GB
            (130, "Chrome", 1 * gib),
            (131, "chrome", 1 * gib / 2),    // Groß- und Kleinschreibung zählt nicht
            (140, "", 9 * gib),              // ohne Namen: weg
        };
        var top = SystemMath.TopPrograms(rows, 2);
        Assert.Equal(2, top.Count);
        Assert.Equal("llama-server", top[0].Name);
        Assert.Equal(5.0, top[0].Gb, 6);
        Assert.Equal("Chrome", top[1].Name);
        Assert.Equal(1.5, top[1].Gb, 6);
    }

    [Fact]
    public void TopProgramsWithZeroCountIsEmpty() =>
        Assert.Empty(SystemMath.TopPrograms(new[] { (200, "app", 1L << 30) }, 0));
}
