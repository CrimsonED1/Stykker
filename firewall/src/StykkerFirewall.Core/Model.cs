namespace StykkerFirewall.Core;

public enum TrafficDirection { Outbound, Inbound }

public enum RuleAction { Allow, Block }

// Monitor: Windows-Standard bleibt (ausgehend erlaubt), nur Blockiertes wird gezeigt.
// Ask: ausgehend ist standardmäßig gesperrt, jede unbekannte App löst eine Nachfrage aus.
public enum ProtectionMode { Monitor, Ask }

// Die vier Antworten im Nachfrage-Dialog.
public enum PromptDecision
{
    AllowApp,      // alles erlauben: jede Verbindung dieser App in dieser Richtung
    AllowTarget,   // nur dieses Ziel: Gegenstelle + Port + Protokoll
    BlockAlways,   // immer blockieren
    BlockOnce,     // diesmal blockieren, keine Regel; beim nächsten Mal (nach der Ruhezeit) wieder fragen
}

// IP-Protokollnummern (IANA). null in einer Regel heißt „jedes Protokoll“.
public static class IpProtocol
{
    public const int Icmp = 1, Tcp = 6, Udp = 17, IcmpV6 = 58;

    public static string Name(int? protocol) => protocol switch
    {
        null => "any",
        Tcp => "TCP",
        Udp => "UDP",
        Icmp => "ICMP",
        IcmpV6 => "ICMPv6",
        _ => protocol.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static bool HasPorts(int? protocol) => protocol is Tcp or Udp;
}

// Ein Verbindungsversuch, den das Backend blockiert hat. Local = dieser Rechner, Remote = Gegenstelle,
// unabhängig von der Richtung.
public sealed record ConnectionAttempt(
    Guid Id,
    DateTimeOffset Time,
    string AppPath,
    int ProcessId,
    TrafficDirection Direction,
    int Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort)
{
    public string AppName => AppPaths.DisplayName(AppPath);
}

// Eine Regel, wie StykkerFirewall sie führt. Das Backend übersetzt sie in seine eigene Form.
// null in RemoteAddress/RemotePort/LocalPort/Protocol heißt „beliebig“.
public sealed record FirewallRule(
    Guid Id,
    string AppPath,
    TrafficDirection Direction,
    RuleAction Action,
    string? RemoteAddress,
    int? RemotePort,
    int? LocalPort,
    int? Protocol,
    DateTimeOffset Created,
    DateTimeOffset? Expires = null,
    string? Note = null)
{
    public bool IsAppWide => RemoteAddress is null && RemotePort is null && LocalPort is null && Protocol is null;

    public bool IsExpired(DateTimeOffset now) => Expires is { } e && e <= now;

    public string AppName => AppPaths.DisplayName(AppPath);
}

public sealed record BackendStatus(
    string BackendId,
    string BackendName,
    bool Available,
    bool FirewallEnabled,
    ProtectionMode Mode,
    bool AuditEnabled,
    string? Problem);

public static class AppPaths
{
    // Pseudo-Pfad, den Windows für den Kernel (PID 4) meldet.
    public const string System = "System";

    public static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
