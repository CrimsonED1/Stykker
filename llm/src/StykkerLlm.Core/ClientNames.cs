namespace StykkerLlm.Core;

// Wer hängt am Server? TCP-Verbindungen zum Server-Port (Besitzer-PID) und Programmname bzw. Pfad der Gegenseite,
// daraus ein hübscher Clientname ("Qwen Desktop, Aider, node ×2").
public sealed class ClientNamer
{
    private readonly IPlatform _platform;
    private readonly Dictionary<(int Pid, long Start), (string Name, DateTime At)> _cache = new();
    private readonly Func<DateTime> _now;

    public ClientNamer(IPlatform platform, Func<DateTime>? now = null)
    {
        _platform = platform;
        _now = now ?? (() => DateTime.UtcNow);
    }

    // Verbindungen von diesem Rechner zum Server-Port: Anzahl je Client-PID. Nur Gegenstellen auf Loopback (127.x bzw. ::1),
    // also wirklich lokale Clients des lokalen Servers.
    public static Dictionary<int, int> ClientPids(IReadOnlyList<ConnectionInfo> connections, int serverPort)
    {
        var result = new Dictionary<int, int>();
        foreach (var c in connections)
        {
            if (c.RemotePort != serverPort || !NetAddr.IsLoopback(c.RemoteAddress)) continue;
            result[c.Pid] = result.GetValueOrDefault(c.Pid) + 1;
        }
        return result;
    }

    public string[] Names(IReadOnlyList<ConnectionInfo> connections, int serverPort, params int[] excludePids)
    {
        var groups = new Dictionary<string, int>();
        foreach (var pid in ClientPids(connections, serverPort).Keys)
        {
            if (excludePids.Contains(pid) || pid == 0) continue;
            var name = PrettyName(pid);
            groups[name] = groups.GetValueOrDefault(name) + 1;
        }
        return groups.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value > 1 ? $"{k.Key} ×{k.Value}" : k.Key).ToArray();
    }

    // Name bevorzugt aus dem Programmpfad; die Kommandozeile wird nur für Interpreter (node, python) herangezogen.
    // Cache: Schlüssel PID + Startzeit, Einträge höchstens 5 Minuten alt.
    public string PrettyName(int pid)
    {
        long start = _platform.ProcessStartTicks(pid) ?? 0;
        var key = (pid, start);
        lock (_cache)
            if (_cache.TryGetValue(key, out var c) && (_now() - c.At).TotalMinutes < 5) return c.Name;

        var d = _platform.ReadProcess(pid);
        var path = d?.ImagePath ?? "";
        var exe = ProcPath.Stem(path);
        if (exe.Length == 0) exe = _platform.ProcessName(pid) ?? "";
        var name = Known(path.ToLowerInvariant());
        if (name == null && exe.ToLowerInvariant() is "node" or "python" or "pythonw" or "python3")
            name = Known((d?.CommandLine ?? "").ToLowerInvariant());
        name ??= exe.Length > 0 ? exe : $"PID {pid}";
        lock (_cache)
        {
            if (_cache.Count > 200) _cache.Clear();
            _cache[key] = (name, _now());
        }
        return name;
    }

    public static string? Known(string hay) =>
        hay.Contains("qwen-code-desktop") ? "Qwen Desktop" :
        hay.Contains("qwen") ? "Qwen Code" :
        hay.Contains("aider") ? "Aider" :
        hay.Contains("hermes") ? "Hermes" :
        hay.Contains("opencode") ? "OpenCode" : null;
}
