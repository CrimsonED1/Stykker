using System.Globalization;
using System.Text;

namespace StykkerLlm.Core;

// Die Tabellen der Modelltests im Proxy-Dialog: der letzte Lauf je Modell aus den CSV-Dateien (auch nach einem Neustart),
// das Überlagern durch den laufenden oder letzten Lauf, die Freigabe der Knöpfe „Use as auto“ und „Use as vision“ und das
// Umschalten „+ Models“. Reine Regeln ohne Oberfläche; die Seite ruft sie auf.
public static class ModelTestTable
{
    // Der Zustand eines „Use as …“-Knopfes in einer Zeile: aktiv, nicht getestet (oder übersprungen) oder getestet und nicht brauchbar
    public enum UseState { Usable, NotTested, NotUsable }

    // Eine CSV-Zeile in ihre Felder (das Gegenstück zu ClassifierTest.Csv: Felder mit „"“ in Anführungszeichen, „""“ ist ein „"“)
    internal static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields;
    }

    // Alle Zeilen einer Test-CSV (die Kopfzeile fällt bei der Zeitprüfung weg). Fehlende oder gesperrte Datei: keine Zeilen.
    internal static List<string[]> ReadCsv(string? path)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrWhiteSpace(path)) return rows;
        string[] lines;
        lock (ClassifierTest.GateFor(path))
        {
            try { lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return rows; }
        }
        foreach (var line in lines)
            if (line.Trim().Length > 0) rows.Add(SplitCsvLine(line).ToArray());
        return rows;
    }

    // Zeit und Anbieter einer Zeile prüfen; null, wenn die Zeile nicht passt (Kopfzeile, Müll, anderer Anbieter)
    private static DateTime? RowTime(string[] f, string url, int fields)
    {
        if (f.Length < fields) return null;
        if (!ProviderKeys.Normalize(f[1]).Equals(url, StringComparison.OrdinalIgnoreCase)) return null;
        return DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    private static double? Seconds(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // Je Modell eines Anbieters die neueste Zeile aus classifier-test.csv (gleiche Zeit: die spätere Zeile der Datei)
    public static IReadOnlyList<ClassifierResult> LastClassifier(string? csvPath, string providerUrl)
    {
        var url = ProviderKeys.Normalize(providerUrl);
        var newest = new Dictionary<string, ClassifierResult>(StringComparer.Ordinal);
        foreach (var f in ReadCsv(csvPath))
        {
            if (RowTime(f, url, 8) is not { } time) continue;
            var fmt = f[6] == "yes" ? true : f[6] == "no" ? (bool?)false : null;
            var row = new ClassifierResult(time, f[2], f[3], Seconds(f[4]), Seconds(f[5]), fmt, f[7]);
            if (!newest.TryGetValue(row.Model, out var old) || row.Time >= old.Time) newest[row.Model] = row;
        }
        return newest.Values.ToList();
    }

    // Je Modell eines Anbieters die neueste Zeile aus vision-test.csv (gleiche Regel wie LastClassifier)
    public static IReadOnlyList<VisionResult> LastVision(string? csvPath, string providerUrl)
    {
        var url = ProviderKeys.Normalize(providerUrl);
        var newest = new Dictionary<string, VisionResult>(StringComparer.Ordinal);
        foreach (var f in ReadCsv(csvPath))
        {
            if (RowTime(f, url, 7) is not { } time) continue;
            var row = new VisionResult(time, f[2], f[3], Seconds(f[4]), f[5] == "yes", f[6]);
            if (!newest.TryGetValue(row.Model, out var old) || row.Time >= old.Time) newest[row.Model] = row;
        }
        return newest.Values.ToList();
    }

    // Die gespeicherten Zeilen, je Modell von den Zeilen des laufenden oder letzten Laufs überlagert (die sind neuer)
    public static IReadOnlyList<T> Overlay<T>(IEnumerable<T> saved, IEnumerable<T> current, Func<T, string> model)
    {
        var byModel = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var r in saved) byModel[model(r)] = r;
        foreach (var r in current) byModel[model(r)] = r;
        return byModel.Values.ToList();
    }

    // Der Knopf „Use as auto“ (Sicherheitsprüfung): nur mit einem brauchbaren Format im Modelltest der Sicherheitsprüfung
    public static UseState AsAuto(IEnumerable<ClassifierResult> classifier, string model)
    {
        var row = classifier.FirstOrDefault(r => r.Model == model && r.Status != Strings.ClassifierStatusSkipped);
        if (row == null) return UseState.NotTested;
        return row.FormatOk is true ? UseState.Usable : UseState.NotUsable;
    }

    // Der Knopf „Use as vision“: nur mit einer richtigen Bildbeschreibung im Bildtest
    public static UseState AsVision(IEnumerable<VisionResult> vision, string model)
    {
        var row = vision.FirstOrDefault(r => r.Model == model && r.Status != Strings.ClassifierStatusSkipped);
        if (row == null) return UseState.NotTested;
        return row.Ok ? UseState.Usable : UseState.NotUsable;
    }

    // Die angebotenen Modelle eines Anbieters mit diesem Modell an- oder abgehakt (Reihenfolge bleibt, Doppelte fallen weg)
    public static string[] WithOffered(IEnumerable<string> offered, string model, bool on)
    {
        var list = offered.Where(m => m != model).ToList();
        if (on) list.Add(model);
        return list.Distinct(StringComparer.Ordinal).ToArray();
    }
}
