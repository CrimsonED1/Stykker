namespace StykkerLlm.Core;

// Die Reihenfolge im Auswahl-Dialog eines Anbieters: die beim Öffnen gewählten Modelle zuerst, dann die übrigen, je
// alphabetisch. Der Suchtext filtert (ohne Groß-/Kleinschreibung), die gewählten bleiben dabei oben. Die Oberfläche
// übergibt als „pinned" die Auswahl von beim Öffnen, damit eine Zeile nicht unter dem Mauszeiger springt, wenn man sie anklickt.
public static class ModelPickOrder
{
    public static IReadOnlyList<string> Arrange(IEnumerable<string> listed, IEnumerable<string> pinned, string? search)
    {
        var pins = new HashSet<string>(pinned.Where(m => !string.IsNullOrEmpty(m)), StringComparer.Ordinal);
        var q = (search ?? "").Trim();
        return listed.Concat(pins)
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct(StringComparer.Ordinal)
            .Where(m => q.Length == 0 || m.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => pins.Contains(m) ? 0 : 1)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
