using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Stykker.Shared.Config;

namespace StykkerHud.Server;

// Startet einen eigenen Dienst der Zentrale (docs/plan-stykkernode.md, Abschnitt 4.1 bis 4.3) und beendet nur den Prozess,
// den sie selbst gestartet hat, über dessen PID (samt den Prozessen, die er selbst gestartet hat: der Qwen-Start
// übergibt den Daemon an einen Kindprozess). Das Token geht nur über die Umgebung, nie über die Kommandozeile und nie
// ins Log.
//
// Endet die Zentrale ohne geordnetes Herunterfahren (Fenster geschlossen, Task-Manager, Absturz), läuft kein Cleanup mehr.
// Darum liegt jeder Dienst in einem Job-Objekt, das seine Prozesse beim Schließen beendet: das System schließt es mit der
// Zentrale. Die Zentrale selbst kommt nie hinein.
internal sealed class ServiceProcesses
{
    private readonly List<Process> _owned = [];
    private readonly Action<string> _log;
    private IntPtr _job = IntPtr.Zero;

    public ServiceProcesses(Action<string> log) => _log = log;

    // Antwortet der Zielport schon, startet nichts: dann läuft, was da ist, und die Zentrale beendet es nicht.
    public async Task StartAsync(ServiceRoute route, CancellationToken cancel = default)
    {
        var start = route.Start;
        if (start == null) return;

        var probe = new HubModule(route.Id, route.Name, route.Port, null, true);
        if (await HubProbe.AnswersAsync(probe, route.Target, cancel))
        {
            _log($"{route.Name}: answers on {route.Target}; not started by the hub.");
            return;
        }

        var info = new ProcessStartInfo(start.Command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Der Zusatz der Zentrale hängt immer an, egal was in args steht
        foreach (var arg in WithoutOwnSwitches(start.Args)) info.ArgumentList.Add(arg);
        info.ArgumentList.Add("--hostname");
        info.ArgumentList.Add("127.0.0.1");
        info.ArgumentList.Add("--require-auth");
        foreach (var (name, value) in start.Env) info.Environment[name] = value;
        info.Environment[start.TokenEnv] = route.Token;

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log($"{route.Name}: could not start {start.Command}: {ex.Message}");
            return;
        }

        // Die Ausgabe ins Log der Zentrale, ohne das Token (falls der Dienst es doch ausgibt)
        string token = route.Token;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => Relay(route.Name, e.Data, token);
        process.ErrorDataReceived += (_, e) => Relay(route.Name, e.Data, token);
        process.Exited += (_, _) => _log($"{route.Name}: stopped (exit code {SafeExitCode(process)}).");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _owned.Add(process);
        Confine(process, route.Name);
        _log($"{route.Name}: started (pid {process.Id}) on {route.Target}.");
    }

    // Beim Ende der Zentrale: nur die eigenen Prozesse (mit ihren Kindern), jeder über seine PID; dann schließt das Job-Objekt.
    public void StopOwned()
    {
        foreach (var process in _owned)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* schon beendet */ }
            finally { process.Dispose(); }
        }
        _owned.Clear();
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);   // mit dem letzten Griff beendet das System alles, was noch im Job läuft
            _job = IntPtr.Zero;
        }
    }

    // --hostname und --require-auth setzt die Zentrale selbst: was in args steht, fällt weg
    internal static IEnumerable<string> WithoutOwnSwitches(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--require-auth", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg.Equals("--hostname", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (arg.StartsWith("--hostname=", StringComparison.OrdinalIgnoreCase)) continue;
            yield return arg;
        }
    }

    // Der Dienst kommt in ein Job-Objekt, das seine Prozesse beim Schließen beendet. Gelingt das nicht, bleibt das Beenden
    // über die PID: die Zentrale meldet es.
    private void Confine(Process process, string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_job == IntPtr.Zero)
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) { _log($"{name}: no job object ({Marshal.GetLastWin32Error()}); the service ends only with its own process."); return; }
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                _log($"{name}: job object not configured ({Marshal.GetLastWin32Error()}).");
                CloseHandle(_job);
                _job = IntPtr.Zero;
                return;
            }
        }
        if (!AssignProcessToJobObject(_job, process.Handle))
            _log($"{name}: not tied to the hub's job object ({Marshal.GetLastWin32Error()}).");
    }

    private void Relay(string name, string? line, string token)
    {
        if (string.IsNullOrEmpty(line)) return;
        _log($"{name}: {line.Replace(token, "***", StringComparison.Ordinal)}");
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
