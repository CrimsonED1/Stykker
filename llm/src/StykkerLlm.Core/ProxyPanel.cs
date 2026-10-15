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

    // Der Anbieter, den ein Modelltest prüft: der gewählte (wenn es ihn noch gibt), sonst der erste mit Schlüssel, sonst der
    // erste; leer = keiner. Gilt je Test (Sicherheitsprüfung und Bild) getrennt.
    public static string TestTarget(IEnumerable<(string Url, bool HasKey)> providers, string? chosen)
    {
        var all = providers.ToList();
        if (chosen != null && all.Any(p => p.Url == chosen)) return chosen;
        return all.FirstOrDefault(p => p.HasKey).Url ?? all.FirstOrDefault().Url ?? "";
    }
}

// Die Reiter des Proxy-Dialogs in ihrer Reihenfolge. Der zuletzt gewählte Reiter bleibt je Browser (ui.js); ein leerer oder
// unbekannter Wert ergibt General.
public static class ProxyDialogTabs
{
    public const string General = "general", Sources = "sources", Routing = "routing", Tests = "tests";
    public static readonly string[] All = { General, Sources, Routing, Tests };

    public static string Parse(string? stored) => Array.IndexOf(All, stored) >= 0 ? stored! : General;
}

// Zähler der Wege seit dem Start des Servers (Zeile „Routed“ der Proxy-Karte), ausgezählt aus den Beobachtungen des Proxys.
// Eine Anfrage, die eine Sicherheitsprüfung in die Cloud schickt, steht in zwei Zählern (auto und cloud).
public sealed class ProxyRouteCounts
{
    private readonly object _lock = new();
    private int _auto, _image, _cloud;

    public void Add(ProxyRecord px)
    {
        if (px.Path is "/v1/models" or "/models") return;   // die Modellliste ist kein Weg zu einem Modell
        lock (_lock)
        {
            if (px.Route == ProxyRoutes.Classifier) _auto++;
            else if (px.Route == ProxyRoutes.Vision) _image++;
            if (ProxyTarget.IsCloudKey(px.ServerKey)) _cloud++;
        }
    }

    public (int Auto, int Image, int Cloud) Snapshot()
    {
        lock (_lock) return (_auto, _image, _cloud);
    }
}
