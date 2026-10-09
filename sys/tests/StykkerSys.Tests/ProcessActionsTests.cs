using System.Diagnostics;
using StykkerSys.Core;

namespace StykkerSys.Tests;

public class ProcessActionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void SystemPidsAreLeftAlone(int pid)
    {
        var result = ProcessActions.End(pid, false);
        Assert.False(result.Ok);
        Assert.Contains("system process", result.Message);
    }

    [Fact]
    public void TheToolItselfIsLeftAlone()
    {
        var result = ProcessActions.End(Environment.ProcessId, false);
        Assert.False(result.Ok);
        Assert.Equal("That is StykkerSYS itself.", result.Message);
    }

    [Fact]
    public void AVanishedProcessIsReportedNotThrown()
    {
        var result = ProcessActions.SetPriority(2147483, "high");
        Assert.False(result.Ok);
        Assert.Contains("no longer running", result.Message);
    }

    [Fact]
    public void AnUnknownPriorityNamesTheKnownOnes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var victim = Victim.Start();
        var result = ProcessActions.SetPriority(victim.Instance.Id, "realtime");
        Assert.False(result.Ok);
        Assert.Equal("Unknown priority \"realtime\". Known: idle, belownormal, normal, abovenormal, high.", result.Message);
    }

    [Fact]
    public void PriorityAndEndWorkOnAProcessNobodyNeeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var victim = Victim.Start();

        var set = ProcessActions.SetPriority(victim.Instance.Id, "belownormal");
        Assert.True(set.Ok, set.Message);
        victim.Instance.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, victim.Instance.PriorityClass);

        var ended = ProcessActions.End(victim.Instance.Id, false);
        Assert.True(ended.Ok, ended.Message);
        Assert.True(victim.Instance.WaitForExit(5000));
    }

    // Ein Prozess, den niemand braucht: ping wartet, bis man ihn beendet. Seine Ausgabe wird nur leer gelesen, damit
    // der Puffer nicht voll läuft. Wer die Prüfung verlässt, beendet ihn in Dispose.
    private sealed class Victim : IDisposable
    {
        public Process Instance { get; }

        private Victim(Process instance) => Instance = instance;

        public static Victim Start()
        {
            var process = Process.Start(new ProcessStartInfo("ping", "-n 120 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }) ?? throw new InvalidOperationException("ping did not start");
            process.OutputDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            return new Victim(process);
        }

        public void Dispose()
        {
            try { if (!Instance.HasExited) Instance.Kill(); }
            catch (InvalidOperationException) { }
            Instance.Dispose();
        }
    }
}
