namespace StykkerLlm.Core;

// ── Datentypen der Plattformschicht ──

// Ein lauschender TCP-Socket. Address roh ("0.0.0.0", "::", "127.0.0.1", "::1" ...); Normalisierung in NetAddr.
public sealed record ListenerInfo(string Address, int Port, int Pid);

public sealed record ConnectionInfo(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, int Pid);

// Was sich über einen Prozess lesen lässt. CommandLine/WorkingDir/Environment sind null bzw. leer, wenn der Zugriff scheitert.
// Environment enthält nur die Umgebungsvariablen der Whitelist (siehe ProcessEnvironment).
public sealed record ProcessDetails(int Pid, long StartTicks, int ParentPid, string? ImagePath, string? CommandLine,
    string? WorkingDir, IReadOnlyDictionary<string, string> Environment);

public sealed record ProcessUsage(double WorkingSetGb, double PrivateGb, TimeSpan CpuTime);

// RamUsedGb = belegter Arbeitsspeicher (gesamt minus verfügbar, wie "In Verwendung" im Task-Manager). Commit ist die zugesagte Menge
// (Arbeitsspeicher + Auslagerungsdatei): nur als Warnung gedacht, nicht als zweiter Hauptwert.
// CoreLoads: Auslastung je logischem Kern in Prozent (U14, nur für den Tooltip), null = nicht gemessen
public sealed record SystemSample(double CpuPercent, int Cores, double RamUsedGb, double RamTotalGb, double CommitUsedGb, double CommitLimitGb,
    double[]? CoreLoads = null)
{
    public const double CommitWarnFrac = 0.85, CommitCriticalFrac = 0.95;

    public double RamFrac => RamTotalGb > 0 ? Math.Clamp(RamUsedGb / RamTotalGb, 0, 1) : 0;
    public double CommitFrac => CommitLimitGb > 0 ? CommitUsedGb / CommitLimitGb : 0;
    // 0 = in Ordnung, 1 = Reserve knapp (ab 85 %), 2 = kritisch (ab 95 %)
    public int CommitLevel => CommitFrac >= CommitCriticalFrac ? 2 : CommitFrac >= CommitWarnFrac ? 1 : 0;
}

public sealed record GpuSample(string Name, double Util, double MemUsedGb, double MemTotalGb, double PowerW, double PowerLimitW, double TempC,
    double MemUtil, double GfxMhz, double GfxMaxMhz, double MemMhz, double MemMaxMhz, ulong ThrottleBits)
{
    public double MemFreeGb => MemTotalGb - MemUsedGb;

    // Drosselgründe als Text (Leerlauf und Anzeigetakt zählen nicht als Drosselung)
    public string ThrottleText()
    {
        var parts = new List<string>();
        if ((ThrottleBits & 0x4) != 0) parts.Add("Power");
        if ((ThrottleBits & 0x20) != 0) parts.Add("Thermal (SW)");
        if ((ThrottleBits & 0x40) != 0) parts.Add("Thermal (HW)");
        if ((ThrottleBits & 0x88) != 0) parts.Add("HW brake");   // 0x8 Hardware-Slowdown, 0x80 Power-Brake
        if (parts.Count == 0) return (ThrottleBits & 0x1) != 0 ? "Idle" : "None";
        return string.Join(", ", parts);
    }

    // Nur echte Begrenzungen (Power, Thermal, Hardware-Bremse); Sync-Boost, App-Takt und Anzeigetakt zählen nicht
    public bool Throttled => (ThrottleBits & ThrottleMask) != 0;
    private const ulong ThrottleMask = 0x4 | 0x8 | 0x20 | 0x40 | 0x80;
}

// GPU-Speicher eines Prozesses (Windows: PDH "GPU Process Memory")
public interface IGpuMemoryQuery : IDisposable
{
    int Pid { get; }
    (double DedicatedGb, double SharedGb)? Read();
}

// Alles, was vom Betriebssystem kommt. Die Windows-Umsetzung liegt im Projekt StykkerLlm.Platform.Windows,
// eine Linux-Umsetzung (/proc, nvml) kann später daneben treten. Alle Methoden dürfen von Hintergrund-Threads
// aufgerufen werden und liefern bei Fehlern null bzw. leere Listen statt Ausnahmen.
public interface IPlatform : IDisposable
{
    int ProcessorCount { get; }
    // Lauschende TCP-Sockets, IPv4 und IPv6 (eine Abfrage pro Durchlauf, das Ergebnis wird geteilt)
    IReadOnlyList<ListenerInfo> ReadListeners();
    // Aufgebaute TCP-Verbindungen, IPv4 und IPv6
    IReadOnlyList<ConnectionInfo> ReadConnections();
    // Startzeit (Ticks, plattformabhängige Einheit) als Zusatzschlüssel gegen wiederverwendete PIDs; null = nicht lesbar
    long? ProcessStartTicks(int pid);
    // Nur Pfad, Startzeit und Elternprozess (eingeschränktes Handle). Liest nie fremden Speicher; für jeden lauschenden Prozess erlaubt.
    ProcessDetails? ReadProcessBasic(int pid);
    // Zusätzlich Kommandozeile, Arbeitsordner und Umgebung (Whitelist) aus dem Prozessspeicher. Nur für Prozesse, die schon als
    // mögliche Modell-Server feststehen (Name oder positive Probe): fremden Speicher zu lesen löst bei Virenscannern Alarm aus.
    ProcessDetails? ReadProcess(int pid);
    string? ProcessName(int pid);
    // Direkte Kindprozesse (PID, Programmname ohne Endung). Standard: keine (Simulator, andere Plattformen).
    IReadOnlyList<(int Pid, string Name)> ChildProcesses(int pid) => Array.Empty<(int, string)>();
    // Alle laufenden Prozesse (PID, Programmname ohne Endung). Standard: keine (andere Plattformen, Simulator).
    IReadOnlyList<(int Pid, string Name)> AllProcesses() => Array.Empty<(int, string)>();
    // Beendet den Prozess über ein einziges Handle: Startzeit prüfen und beenden ohne Zeitfenster dazwischen.
    // expectedStartTicks == 0: nicht prüfen (der Aufrufer hat das ausdrücklich bestätigen lassen).
    StopOutcome Terminate(int pid, long expectedStartTicks, out string? error);
    ProcessUsage? ReadUsage(int pid);
    IGpuMemoryQuery? OpenGpuMemory(int pid);
    GpuSample? ReadGpu();
    SystemSample? ReadSystem();
    // Arbeitsspeicher laut Firmware (Typ, Takt, Module; U14). Standard: unbekannt.
    MemoryInfo? ReadMemoryInfo() => null;
    // Treiberversion der GPU (für den Benchmark-Fingerprint). Standard: unbekannt (z. B. Demo/andere Plattformen).
    string? GpuDriver => null;
    // größte VRAM-Belegungen je Prozess (teuer: höchstens alle paar Sekunden)
    IReadOnlyList<(string Name, double Gb)>? ReadGpuTop(int count);
    // größte RAM-Belegungen (Working Set) je Programm, gleichnamige Prozesse zusammengefasst. Standard: nicht verfügbar.
    IReadOnlyList<(string Name, double Gb)>? ReadRamTop(int count) => null;
    // GPU-Auslastung je Prozess in Prozent (über alle Engines summiert), größte zuerst. Standard: nicht verfügbar.
    IReadOnlyList<(string Name, double Percent)> ReadGpuUtilTop(int count) => Array.Empty<(string, double)>();
    // Daten an den aktuellen Benutzer binden (Windows: DPAPI CurrentUser). null = nicht möglich (Standard, z. B. Linux/Simulator):
    // der Aufrufer speichert dann nichts dauerhaft.
    byte[]? ProtectForCurrentUser(byte[] data) => null;
    byte[]? UnprotectForCurrentUser(byte[] data) => null;
}

// Adress-Hilfen für Listener (IPv4 und IPv6)
public static class NetAddr
{
    public static bool IsAny(string a) => a is "0.0.0.0" or "::" or "0:0:0:0:0:0:0:0";

    public static bool IsLoopback(string a) => a.StartsWith("127.", StringComparison.Ordinal) || a is "::1" or "0:0:0:0:0:0:0:1";

    // Der Host-Kopf einer Anfrage zeigt auf diesen PC: Loopback-Namen; mit Tailscale-Schalter auch die Tailnet-IP (als IP,
    // kein Domainname, damit ein fremder Name nicht durchrutscht). Ohne Host-Kopf (HTTP/1.0) gilt die Anfrage als eigene.
    public static bool IsOwnHost(string? hostHeader, bool tailscale)
    {
        var value = hostHeader ?? "";
        if (value.Length == 0) return true;
        string name;
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            name = close > 0 ? value[1..close] : value;
        }
        else name = value.Contains(':') ? value[..value.IndexOf(':')] : value;
        if (name is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0") return true;
        return tailscale && IsTailscale(name);
    }

    // Tailscale: IPv4 aus 100.64.0.0/10 (CGNAT), IPv6 aus fd7a:115c:a1e0::/48. IPv4 in IPv6 (::ffff:…) wird ausgepackt.
    public static bool IsTailscale(string a)
    {
        if (a.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) a = a[7..];
        if (a.StartsWith("fd7a:115c:a1e0:", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = a.Split('.');
        return parts.Length == 4 && parts[0] == "100" && int.TryParse(parts[1], out var b) && b is >= 64 and <= 127;
    }

    public static bool IsV6(string a) => a.Contains(':');

    // Adresse, unter der sich ein lokaler Listener ansprechen lässt: 0.0.0.0 -> 127.0.0.1, :: -> ::1
    public static string Reachable(string a) => a switch
    {
        "0.0.0.0" => "127.0.0.1",
        "::" or "0:0:0:0:0:0:0:0" => "::1",
        "0:0:0:0:0:0:0:1" => "::1",
        _ => a,
    };

    // http://127.0.0.1:8081 bzw. http://[::1]:8081
    public static string Url(string host, int port) => IsV6(host) ? $"http://[{host}]:{port}" : $"http://{host}:{port}";

    public static string Key(string host, int port) => IsV6(host) ? $"[{host}]:{port}" : $"{host}:{port}";
}

// Programmname aus einem Prozesspfad. Trennt an '\' und '/', damit Windows-Pfade (auch die des Simulators) unter Linux
// genauso gelesen werden wie unter Windows: "C:\llama\llama-server.exe" -> "llama-server"
public static class ProcPath
{
    public static string Stem(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return Path.GetFileNameWithoutExtension(cut >= 0 ? path[(cut + 1)..] : path);
    }
}
