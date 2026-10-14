using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using StykkerStarter.Core.Processes;
using StykkerStarter.Core.Start;

namespace StykkerStarter.Platform.Windows;

// Prozesse unter Windows. Der Programmpfad kommt über QueryFullProcessImageNameW mit PROCESS_QUERY_LIMITED_INFORMATION
// (Vorbild: sys/src/StykkerSys.Platform.Windows/WindowsProcessProbe.cs). Beendet wird nur über die PID.
public sealed class WindowsProcessSystem : IProcessSystem
{
    private const uint QueryLimitedInformation = 0x1000;

    public IReadOnlyList<ProcessInfo> List()
    {
        var result = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try { result.Add(new ProcessInfo(process.Id, ImagePath(process.Id))); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            finally { process.Dispose(); }
        }
        return result;
    }

    public bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public bool CloseMainWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
}

// Startet ein Programm als eigenen Prozess mit dem Arbeitsordner, den der Aufrufer nennt.
public sealed class WindowsProgramLauncher : IProgramLauncher
{
    public IStartedProgram Start(string exePath, string arguments, string workingDir)
    {
        var info = new ProcessStartInfo(exePath)
        {
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException("the program did not start");
        return new Started(process);
    }

    private sealed class Started(Process process) : IStartedProgram
    {
        public int Pid => process.Id;

        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode => HasExited ? SafeExitCode() : null;

        private int? SafeExitCode()
        {
            try { return process.ExitCode; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
