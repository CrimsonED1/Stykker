using System.Globalization;
using System.Xml.Linq;
using StykkerFirewall.Core;

namespace StykkerFirewall.Backend.WindowsFirewall;

// Ereignis 5157 „Die Windows-Filterplattform hat eine Verbindung blockiert“ als XML → ConnectionAttempt.
// Die Feldnamen im XML sind nicht übersetzt, die Richtung steht als Nachrichtenkennung (%%14592/%%14593) drin –
// beides funktioniert auf deutschem wie englischem Windows.
// Source* ist immer dieser Rechner, Dest* die Gegenstelle, auch bei eingehenden Verbindungen.
public static class Audit5157Parser
{
    public const int EventId = 5157;
    private const string DirectionInbound = "%%14592";
    private const string DirectionOutbound = "%%14593";

    public static ConnectionAttempt? Parse(string xml, Func<string, string> resolveDevicePath)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        var root = doc.Root;
        if (root is null) return null;
        XNamespace ns = root.Name.Namespace;

        var data = root.Element(ns + "EventData")?.Elements(ns + "Data")
            .Where(d => d.Attribute("Name") is not null)
            .GroupBy(d => (string)d.Attribute("Name")!)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        if (data is null) return null;

        var direction = Get(data, "Direction") switch
        {
            DirectionInbound => TrafficDirection.Inbound,
            DirectionOutbound => TrafficDirection.Outbound,
            _ => (TrafficDirection?)null,
        };
        if (direction is null) return null;

        var timeText = root.Element(ns + "System")?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        var time = DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t : DateTimeOffset.UtcNow;

        var app = Get(data, "Application");
        return new ConnectionAttempt(
            Guid.NewGuid(),
            time,
            string.IsNullOrEmpty(app) ? "" : resolveDevicePath(app),
            Int(data, "ProcessID"),
            direction.Value,
            Int(data, "Protocol"),
            Get(data, "SourceAddress"),
            Int(data, "SourcePort"),
            Get(data, "DestAddress"),
            Int(data, "DestPort"));
    }

    private static string Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v.Trim() : "";

    private static int Int(Dictionary<string, string> d, string key) =>
        int.TryParse(Get(d, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
