using System.Security.Cryptography;
using System.Text;

namespace StykkerLlm.Core;

// Nur eine Instanz je Datenordner (zwei Instanzen würden library.json, settings.json und Aufnahmen gegenseitig überschreiben).
// Die erste hält einen benannten Mutex und wartet auf ein Signal; die zweite löst das Signal aus (ActivationRequested der ersten:
// vorhandenes Fenster nach vorn bringen) und beendet sich. Der Name hängt vom Datenordner ab: Testläufe mit --data-dir stören sich nicht.
// Unter Linux/macOS gibt es keine benannten EventWaitHandles: dort hält die erste Instanz eine gesperrte Datei
// (FileShare.None = flock) und das Signal ist eine Datei "<name>-show", die die erste Instanz abfragt.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _signal;
    private readonly FileStream? _lock;      // nur außerhalb von Windows
    private readonly string? _showFile;      // nur außerhalb von Windows
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread? _thread;
    private bool _disposed;

    public bool IsFirst { get; }
    // Eine weitere Instanz wurde gestartet (kommt auf einem Hintergrund-Thread an)
    public event Action? ActivationRequested;

    public SingleInstance(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            _mutex = new Mutex(true, "Local\\" + name, out bool created);
            IsFirst = created;
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + name + "-show");
        }
        else
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.GetTempPath();
            _showFile = Path.Combine(dir, name + "-show");
            try
            {
                _lock = new FileStream(Path.Combine(dir, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                IsFirst = true;
                try { File.Delete(_showFile); } catch { }   // altes Signal einer früheren Sitzung
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { IsFirst = false; }
        }
        if (IsFirst)
        {
            _thread = new Thread(Listen) { IsBackground = true, Name = "SingleInstance" };
            _thread.Start();
        }
        else { try { _mutex?.Dispose(); } catch { } _mutex = null; }
    }

    // Name aus dem Datenordner: gleicher Ordner = gleiche Instanz
    public static string NameFor(string dataDir)
    {
        string full;
        try { full = Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant(); } catch { full = dataDir.ToLowerInvariant(); }
        return "StykkerLLM-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..12];
    }

    // Nur das Fenster nimmt diesen Mutex: es gibt nur ein Fenster je Datenordner. Der StykkerLLM-Server darf ihn
    // ausdrücklich NICHT nehmen – er ist seit S4 die Engine und schreibt immer. Tat er es doch, blockierte ein
    // zuerst gestarteter Server das Fenster (das sich lautlos beendete) und umgekehrt lief ein vom Fenster
    // gestarteter Server nur lesend. Gegen einen zweiten Serverprozess schützt der eigene Mutex in Program.cs.

    private void Listen()
    {
        if (_signal == null) { PollShowFile(); return; }
        var handles = new[] { _signal, _cts.Token.WaitHandle };
        while (true)
        {
            int i = WaitHandle.WaitAny(handles);
            if (i != 0 || _disposed) break;
            try { ActivationRequested?.Invoke(); } catch { }
        }
    }

    private void PollShowFile()
    {
        while (!_cts.Token.WaitHandle.WaitOne(250))
        {
            if (_disposed) break;
            try
            {
                if (!File.Exists(_showFile)) continue;
                File.Delete(_showFile!);
            }
            catch { continue; }
            try { ActivationRequested?.Invoke(); } catch { }
        }
    }

    // Die erste Instanz auffordern, ihr Fenster nach vorn zu bringen
    public void SignalFirst()
    {
        try
        {
            if (_signal != null) _signal.Set();
            else if (_showFile != null) File.WriteAllText(_showFile, "");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _thread?.Join(500); } catch { }
        try { if (IsFirst) _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        try { _signal?.Dispose(); } catch { }
        try { _lock?.Dispose(); } catch { }
    }
}
