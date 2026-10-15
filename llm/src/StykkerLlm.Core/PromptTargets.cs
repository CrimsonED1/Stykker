using System.Globalization;

namespace StykkerLlm.Core;

// Das Ziel der Prompt-Seite, wenn sie über den Stykker-Proxy geht: die Adresse des lokalen Proxys und der Modellname, unter dem
// der Proxy das Ziel kennt (Cloud: „<Anbieter>/<Modell>“; „stykker“ = das aktuelle Ziel des Proxys). Den Schlüssel setzt der
// Proxy selbst dazu; die Seite sieht ihn nie.
public sealed record PromptTarget(string Url, string Model);

// Die Einträge „Via Stykker-Proxy“ der Auswahl auf der Prompt-Seite und ihre Auflösung zu einem Ziel
public static class PromptTargets
{
    // Präfix der Auswahlwerte, die über den Proxy gehen (lokale Server-Keys tragen es nicht)
    public const string ViaPrefix = "via:";

    public static string ViaValue(string choiceValue) => ViaPrefix + choiceValue;

    public static bool IsVia(string value) => value.StartsWith(ViaPrefix, StringComparison.Ordinal);

    // Die Adresse des lokalen Proxys (ohne /v1; die Prompt-Seite hängt den Pfad selbst an)
    public static string ProxyUrl(int port) => "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

    // Die Einträge der Gruppe: zuerst das aktuelle Ziel des Proxys, dann die Cloud-Modelle der Proxy-Auswahl (Wert, Anzeige).
    // Lokale Modelle stehen nicht darin: die laufen über ihren eigenen Server-Eintrag.
    public static IReadOnlyList<(string Value, string Label)> Options(IEnumerable<ProxyChoice> choices) =>
        new[] { (ViaValue(RouterProxy.VirtualModel), Strings.PromptViaCurrent) }
            .Concat(choices.Where(c => c.Cloud).Select(c => (ViaValue(c.Value), c.Display)))
            .ToList();

    // Der Modellname eines Eintrags der Gruppe, oder null wenn der Eintrag nicht (mehr) angeboten wird
    public static string? ModelOf(string value, IEnumerable<ProxyChoice> choices)
    {
        if (!IsVia(value)) return null;
        var choice = value[ViaPrefix.Length..];
        if (choice.Equals(RouterProxy.VirtualModel, StringComparison.Ordinal)) return RouterProxy.VirtualModel;
        return choices.FirstOrDefault(c => c.Cloud && c.Value == choice)?.Model;
    }

    // Das Ziel für einen Eintrag der Gruppe. Läuft der Proxy nicht, kommt eine Meldung statt eines Ziels: nichts scheitert still.
    public static (PromptTarget? Target, string? Error) Resolve(string value, IEnumerable<ProxyChoice> choices, int port, bool proxyRunning)
    {
        if (!proxyRunning) return (null, Strings.ClientProxyOff);
        var model = ModelOf(value, choices);
        return model == null ? (null, Strings.PromptViaGone) : (new PromptTarget(ProxyUrl(port), model), null);
    }
}
