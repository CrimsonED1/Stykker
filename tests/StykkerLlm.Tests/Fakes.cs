using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Plattform mit frei einstellbaren Prozessen und Verbindungen
internal sealed class FakePlatform : IPlatform
{
    public List<ListenerInfo> Listeners { get; } = new();
    public List<ConnectionInfo> Connections { get; } = new();
    public Dictionary<int, ProcessDetails> Processes { get; } = new();
    public int ReadProcessCalls;

    public int ProcessorCount => 8;
    public IReadOnlyList<ListenerInfo> ReadListeners() => Listeners.ToList();
    public IReadOnlyList<ConnectionInfo> ReadConnections() => Connections.ToList();
    public long? ProcessStartTicks(int pid) => Processes.TryGetValue(pid, out var d) ? d.StartTicks : null;
    public int ReadBasicCalls;
    public ProcessDetails? ReadProcessBasic(int pid) { ReadBasicCalls++; return Processes.TryGetValue(pid, out var d) ? d with { CommandLine = null, WorkingDir = null, Environment = new Dictionary<string, string>() } : null; }
    public ProcessDetails? ReadProcess(int pid) { ReadProcessCalls++; return Processes.GetValueOrDefault(pid); }
    // Beendet-Aufrufe (PID, erwartete Startzeit); Prozesse mit passender Startzeit verschwinden aus Processes und Listeners
    public List<(int Pid, long Ticks)> Terminated { get; } = new();
    public StopOutcome Terminate(int pid, long expectedStartTicks, out string? error)
    {
        error = null;
        if (!Processes.TryGetValue(pid, out var d)) return StopOutcome.NotFound;
        if (expectedStartTicks != 0 && d.StartTicks != expectedStartTicks) return StopOutcome.ProcessChanged;
        Terminated.Add((pid, expectedStartTicks));
        Processes.Remove(pid);
        Listeners.RemoveAll(l => l.Pid == pid);
        return StopOutcome.Stopped;
    }
    public string? ProcessName(int pid) => Processes.TryGetValue(pid, out var d) && d.ImagePath != null ? ProcPath.Stem(d.ImagePath) : null;
    public ProcessUsage? ReadUsage(int pid) => null;
    // VRAM je PID (dediziert, geteilt), z. B. für LM-Studio-Kinder
    public Dictionary<int, (double Dedicated, double Shared)> GpuMem { get; } = new();
    public IGpuMemoryQuery? OpenGpuMemory(int pid) => GpuMem.TryGetValue(pid, out var m) ? new FakeGpuQuery(pid, m) : null;
    public GpuSample? ReadGpu() => null;
    public SystemSample? ReadSystem() => null;
    public IReadOnlyList<(string Name, double Gb)>? ReadGpuTop(int count) => null;
    // GPU-Auslastung je Prozess (S4: die Liste im GPU-Details kommt aus dem Zustand, nicht aus einer eigenen Messung)
    public IReadOnlyList<(string Name, double Percent)> GpuUtilTop { get; set; } = Array.Empty<(string, double)>();
    public IReadOnlyList<(string Name, double Percent)> ReadGpuUtilTop(int count) => GpuUtilTop;
    // Benutzerbindung (DPAPI) nachgebildet: aus = wie Linux/Simulator (null); an = einfaches XOR, ein fremder "Blob" ist nicht lesbar
    public bool UserProtection { get; set; }
    public byte[]? ProtectForCurrentUser(byte[] data) => UserProtection ? data.Select(b => (byte)(b ^ 0x5A)).Concat(new byte[] { 0xA5 }).ToArray() : null;
    public byte[]? UnprotectForCurrentUser(byte[] data) => UserProtection && data.Length > 0 && data[^1] == 0xA5 ? data[..^1].Select(b => (byte)(b ^ 0x5A)).ToArray() : null;
    public void Dispose() { }

    public void AddServer(int pid, string image, string? cmdline, int port, string address = "0.0.0.0", int parent = 1000,
        string? cwd = @"C:\work", IReadOnlyDictionary<string, string>? env = null, long start = 111)
    {
        Processes[pid] = new ProcessDetails(pid, start, parent, image, cmdline, cwd, env ?? new Dictionary<string, string>());
        Listeners.Add(new ListenerInfo(address, port, pid));
    }
}

// HTTP-Handler mit Antworten je Host:Port/Pfad und Zähler
internal sealed class FakeHandler : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Code, string Body)> Routes { get; } = new();
    // Antwort in Abhängigkeit von Pfad und Anfragetext (hat Vorrang vor Routes); null = Routes verwenden
    public Func<string, string?, (HttpStatusCode Code, string Body)?>? Responder { get; set; }
    public List<string> Requests { get; } = new();
    public List<string?> AuthHeaders { get; } = new();
    public List<string> Posts { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = request.RequestUri!.Authority + request.RequestUri.AbsolutePath;
        string? body = null;
        if (request.Content != null) body = request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        lock (Requests) Posts.Add(request.Method + " " + key + " " + body);
        lock (Requests) { Requests.Add(key); AuthHeaders.Add(request.Headers.Authorization?.ToString()); }
        if (Responder?.Invoke(key, body) is { } dyn)
            return Task.FromResult(new HttpResponseMessage(dyn.Code) { Content = new StringContent(dyn.Body) });
        if (Routes.TryGetValue(key, out var r))
            return Task.FromResult(new HttpResponseMessage(r.Code) { Content = new StringContent(r.Body) });
        throw new HttpRequestException("connection refused");
    }
}

internal sealed class FakeGpuQuery : IGpuMemoryQuery
{
    private readonly (double Dedicated, double Shared) _m;
    public FakeGpuQuery(int pid, (double Dedicated, double Shared) m) { Pid = pid; _m = m; }
    public int Pid { get; }
    public (double DedicatedGb, double SharedGb)? Read() => _m;
    public void Dispose() { }
}

internal static class Samples
{
    public const string BonsaiCmd =
        "\"C:\\ai\\bonsai\\llama\\llama-server.exe\" -m C:\\ai\\bonsai\\models\\Bonsai-27B-Q1_0.gguf --host 127.0.0.1 " +
        "--port 8081 --alias bonsai-27b-1bit -ngl 99 -fa on -c 65536 -ctk q4_0 -ctv q4_0 -np 1 --jinja --temp 1.0 " +
        "--top-p 0.95 --top-k 20 --min-p 0.05 --log-file C:\\ai\\bonsai\\server-1bit.log";

    public const string PropsJson = "{\"model_path\":\"C:\\\\m\\\\x.gguf\",\"build_info\":\"b1234\",\"total_slots\":1,\"model_alias\":\"x\",\"default_generation_settings\":{\"n_ctx\":4096}}";
}
