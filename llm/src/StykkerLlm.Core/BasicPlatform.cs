namespace StykkerLlm.Core;

// Plattform ohne Betriebssystem-Zugriff: keine Listener, Prozesse, GPU- oder Systemwerte.
// Sie liegt im Core, weil sie nicht nur die CLI braucht – auch der Server (StykkerLLM-Server.exe) wählt sie
// auf einem System, das nicht Windows ist, damit er dort überhaupt startet.
//
// Was dadurch geht: der Server läuft, die Weboberfläche, die Bibliothek, der Verlauf, Aufnahmen, Benchmarks und
// die Modelltests; dazu jeder von Hand eingetragene Server („Add server by URL“), gemessen über die Backends.
// Was fehlt: die automatische Erkennung (keine Listener/Prozesse), GPU- und Systemwerte, das Beenden von
// Prozessen und die Bindung der Dateien an den Benutzer (kein DPAPI außerhalb von Windows).
// Eine echte Linux-Umsetzung von IPlatform (/proc/net/tcp, /proc/<pid>, nvidia-smi) ist noch nicht gebaut;
// bis dahin gilt dieser Ersatz.
public sealed class BasicPlatform : IPlatform
{
    public int ProcessorCount => Environment.ProcessorCount;
    public IReadOnlyList<ListenerInfo> ReadListeners() => Array.Empty<ListenerInfo>();
    public IReadOnlyList<ConnectionInfo> ReadConnections() => Array.Empty<ConnectionInfo>();
    public long? ProcessStartTicks(int pid) => null;
    public ProcessDetails? ReadProcessBasic(int pid) => null;
    public ProcessDetails? ReadProcess(int pid) => null;
    public string? ProcessName(int pid) => null;
    public StopOutcome Terminate(int pid, long expectedStartTicks, out string? error) { error = "not supported on this platform"; return StopOutcome.Failed; }
    public ProcessUsage? ReadUsage(int pid) => null;
    public IGpuMemoryQuery? OpenGpuMemory(int pid) => null;
    public GpuSample? ReadGpu() => null;
    public SystemSample? ReadSystem() => null;
    public IReadOnlyList<(string Name, double Gb)>? ReadGpuTop(int count) => null;
    public void Dispose() { }
}