using System.Globalization;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Rules;

namespace StykkerFirewall.App.Components;

// Anzeige-Texte, an einer Stelle, damit Übersicht und Nachfrage gleich sprechen.
public static class Format
{
    public static string Direction(TrafficDirection d) => d == TrafficDirection.Inbound ? "in" : "out";

    public static string Endpoint(string address, int port, int protocol) =>
        IpProtocol.HasPorts(protocol) ? (address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}") : address;

    public static string Target(FirewallRule r)
    {
        if (r.IsAppWide) return "any";
        var parts = new List<string>();
        if (r.RemoteAddress is not null) parts.Add(r.RemoteAddress);
        if (r.RemotePort is { } rp) parts.Add("port " + rp.ToString(CultureInfo.InvariantCulture));
        if (r.LocalPort is { } lp) parts.Add("local port " + lp.ToString(CultureInfo.InvariantCulture));
        parts.Add(IpProtocol.Name(r.Protocol));
        return string.Join(" · ", parts);
    }

    public static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Date(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Verdict(GateVerdict v) => v switch
    {
        GateVerdict.Prompt => "asked",
        GateVerdict.AlreadyPending => "waiting",
        GateVerdict.QuietAfterBlock => "blocked once",
        GateVerdict.CoveredByRule => "by rule",
        GateVerdict.NotPromptable => "system",
        _ => v.ToString(),
    };
}
