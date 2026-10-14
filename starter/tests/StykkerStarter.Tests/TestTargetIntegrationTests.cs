using System.Diagnostics;
using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Update;
using StykkerStarter.Core.Versions;
using StykkerStarter.Platform.Windows;

namespace StykkerStarter.Tests;

// Der ganze Ablauf mit echten Prozessen: StykkerStarter.TestTarget wird in zwei Versionen gebaut (Dateiversion
// über TargetVersion), kopiert, gestartet, als laufend erkannt, aktualisiert, die alte beendet und aufgeräumt.
// Beendet wird nur, was der Ablauf selbst gestartet hat (über die PID, die er kennt), nie über den Namen.
public class TestTargetIntegrationTests : IDisposable
{
    private const string ExeName = "StykkerStarter.TestTarget.exe";
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Install_update_and_cleanup_with_the_real_test_program()
    {
        var layout = new DataLayout(_temp.Sub("data"));
        var source = _temp.Sub("source");
        CopyFolder(BuildTestTarget("1.0.0.0", _temp.Sub("build-1")), source);
        var exe = Path.Combine(source, ExeName);
        var program = new ProgramEntry { Id = "target", Name = "Test target", SourceExe = exe };
        var processes = new WindowsProcessSystem();
        var timing = new ProgramTiming(CloseGrace: TimeSpan.FromSeconds(1), StartWatch: TimeSpan.FromMilliseconds(1500));
        var service = new ProgramService(layout, processes, new WindowsProgramLauncher(), null, timing, Environment.ProcessId);
        try
        {
            var tree1 = SourceTree.Read(exe);
            var plan1 = VersionPlanner.Plan(program, tree1, new FileVersionReader().Read(exe), layout);
            Assert.Equal("1.0.0.0", plan1.Version);

            var install = service.Install(program, tree1, plan1);
            Assert.True(install.Kind == UpdateKind.Updated, install.Message);
            var first = Assert.Single(service.RunningProcesses(program));

            // Version 2 in die Quelle; die laufende Kopie der Version 1 ist davon nicht betroffen
            CopyFolder(BuildTestTarget("2.0.0.0", _temp.Sub("build-2")), source);
            var tree2 = SourceTree.Read(exe);
            var plan2 = VersionPlanner.Plan(program, tree2, new FileVersionReader().Read(exe), layout);
            Assert.Equal("2.0.0.0", plan2.Version);
            Assert.Equal(PlanKind.Copy, plan2.Kind);

            var update = service.Update(program, tree2, plan2);

            Assert.True(update.Kind == UpdateKind.Updated, update.Message);
            Assert.Contains(first.Pid, update.StoppedPids);
            Assert.False(processes.IsRunning(first.Pid));
            var second = Assert.Single(service.RunningProcesses(program));
            Assert.NotEqual(first.Pid, second.Pid);
            Assert.StartsWith(layout.VersionDir(program.Id, "2.0.0.0"), second.ImagePath!, StringComparison.OrdinalIgnoreCase);

            var cleanup = service.Cleanup(program, versionsKept: 1);

            Assert.Contains("1.0.0.0", cleanup.Removed);
            Assert.False(Directory.Exists(layout.VersionDir(program.Id, "1.0.0.0")));
            Assert.True(Directory.Exists(layout.VersionDir(program.Id, "2.0.0.0")));
        }
        finally
        {
            // Nur die Kopie der laufenden Version; sie hat der Ablauf selbst gestartet
            service.Stop(program);
        }
    }

    private static string BuildTestTarget(string version, string outDir)
    {
        var project = Path.Combine(FindStarterRoot(), "tests", "StykkerStarter.TestTarget", "StykkerStarter.TestTarget.csproj");
        var info = new ProcessStartInfo("dotnet",
            $"build \"{project}\" -c Debug --no-restore -nologo -v q -p:TargetVersion={version} -o \"{outDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var build = Process.Start(info)!;
        var errors = build.StandardError.ReadToEndAsync();
        var output = build.StandardOutput.ReadToEnd();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, output + errors.Result);
        Assert.True(File.Exists(Path.Combine(outDir, ExeName)));
        return outDir;
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

    // Der Starter-Ordner (mit der .slnx) liegt über der Testassembly
    private static string FindStarterRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "StykkerStarter.slnx"))) return dir.FullName;
        throw new InvalidOperationException("the starter folder was not found above the test assembly");
    }
}
