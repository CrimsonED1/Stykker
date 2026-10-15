using System.Globalization;

namespace StykkerLlm.Core;

// Werte für die Proxy-Karte im Monitor und den Proxy-Dialog: was dort steht, rechnet hier und nicht in der Seite.
public static class ProxyPanel
{
    // Adresse für Clients: im LAN die erste Netzadresse dieses PCs, sonst 127.0.0.1
    public static string BaseUrl(ProxyManager px)
    {
        var host = px.BindLan ? NetInfo.Ipv4Addresses().FirstOrDefault() ?? "127.0.0.1" : "127.0.0.1";
        return $"http://{host}:{px.Port.ToString(CultureInfo.InvariantCulture)}/v1";
    }

    // Modelle dieses PCs in der Auswahl: ohne Cloud-Modelle und ohne die der angehängten Stykker-Rechner
    public static int LocalModelCount(IEnumerable<ProxyChoice> choices) =>
        choices.Count(c => !c.Cloud && !c.Value.StartsWith("remote:", StringComparison.Ordinal));

    // Das Prüfmodell als Name: leer = wie das Ziel des Proxys; sonst der Anzeigename aus der Auswahl, und wenn das
    // gewählte Modell gerade nicht in der Auswahl steht, der gespeicherte Wert
    public static string ClassifierLabel(string value, IEnumerable<ProxyChoice> choices)
    {
        if (value.Length == 0) return Strings.ClientAutoSame;
        return choices.FirstOrDefault(c => c.Value == value)?.Display ?? value;
    }
}
