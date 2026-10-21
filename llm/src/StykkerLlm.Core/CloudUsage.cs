using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Verbrauch der Cloud-Anbieter je Kalendertag (lokale Zeit): Anfragen, Fehler (HTTP ≥ 400) und Ausgabe-Token
// (Antwort und Denken). Nur Zahlen je Anbieter-Adresse: kein Modellinhalt, keine Schlüssel. Die Datei cloud-usage.json
// im Datenordner hält höchstens KeepDays Tage. Schreiben ist gedrosselt; Flush beim Beenden schreibt den Rest.
// Prompt-Token fehlen: der Proxy liest sie aus der Antwort nicht (ProxyRecord kennt sie nicht).
public sealed class CloudUsage
{
    public const int KeepDays = 90;
    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);
    private const string DayFormat = "yyyy-MM-dd";
    private const int Version = 1;

    // Zähler eines Tages
    public sealed class Counts
    {
        public long Requests { get; set; }
        public long Errors { get; set; }
        public long OutTokens { get; set; }
    }

    // Stand für einen Anbieter: heute und die letzten sieben Tage (heute eingeschlossen)
    public sealed record Totals(string Url, long TodayRequests, long TodayTokens, long TodayErrors, long WeekRequests, long WeekTokens, long WeekErrors)
    {
        public bool HasUse => WeekRequests > 0;
    }

    private readonly object _lock = new();
    private readonly string? _file;
    private readonly Func<DateTime> _now;
    // Anbieter-Adresse → Tag (yyyy-MM-dd) → Zähler
    private readonly Dictionary<string, SortedDictionary<string, Counts>> _days = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;

    // file = null: nur im Speicher (Lesemodus, Tests). now = Uhr (Tests)
    public CloudUsage(string? file, Func<DateTime>? now = null)
    {
        _file = file;
        _now = now ?? (() => DateTime.Now);
        Load();
    }

    // Eine Anfrage an einen Cloud-Anbieter zählen. Lokale Ziele (kein provider:-Schlüssel) und die Modellliste zählen nicht.
    public void Add(ProxyRecord px)
    {
        if (!ProxyTarget.IsCloudKey(px.ServerKey)) return;
        var url = ProxyManager.ProviderBaseUrl(px.ServerKey);
        if (url.Length == 0) return;
        if (px.Path is "/v1/models" or "/models") return;
        lock (_lock)
        {
            if (!_days.TryGetValue(url, out var days)) _days[url] = days = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
            var day = px.End.ToString(DayFormat, CultureInfo.InvariantCulture);
            if (!days.TryGetValue(day, out var c)) days[day] = c = new Counts();
            c.Requests++;
            if (px.HttpStatus >= 400) c.Errors++;
            if (px.ContentTokens > 0) c.OutTokens += px.ContentTokens;
            if (px.ReasoningTokens > 0) c.OutTokens += px.ReasoningTokens;
            Prune(_now());
            _dirty = true;
            if (_now() - _lastSave >= SaveEvery) SaveLocked();
        }
    }

    // Stand eines Anbieters (null, wenn er nie genutzt wurde)
    public Totals? Get(string url)
    {
        lock (_lock)
        {
            var key = _days.Keys.FirstOrDefault(k => string.Equals(k, url, StringComparison.OrdinalIgnoreCase));
            return key == null ? null : Sum(key, _days[key], _now());
        }
    }

    // Stand aller Anbieter, nach Adresse sortiert
    public IReadOnlyList<Totals> Snapshot()
    {
        lock (_lock)
        {
            var now = _now();
            return _days.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => Sum(k, _days[k], now)).ToList();
        }
    }

    // Schreibt alles, was noch nicht auf der Platte steht (beim Beenden)
    public void Flush()
    {
        lock (_lock) SaveLocked();
    }

    // Tokens lesbar: 999, 1.2k, 34.5k, 1.5M
    public static string FormatTokens(long n)
    {
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 1_000_000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    private static Totals Sum(string url, SortedDictionary<string, Counts> days, DateTime now)
    {
        var today = now.ToString(DayFormat, CultureInfo.InvariantCulture);
        var weekFrom = now.Date.AddDays(-6).ToString(DayFormat, CultureInfo.InvariantCulture);
        long tr = 0, tt = 0, te = 0, wr = 0, wt = 0, we = 0;
        foreach (var (day, c) in days)
        {
            if (string.CompareOrdinal(day, weekFrom) < 0) continue;
            wr += c.Requests; we += c.Errors; wt += c.OutTokens;
            if (day == today) { tr = c.Requests; te = c.Errors; tt = c.OutTokens; }
        }
        return new Totals(url, tr, tt, te, wr, wt, we);
    }

    // Tage vor dem Fenster (KeepDays Tage inklusive heute) und leere Anbieter fallen weg
    private void Prune(DateTime now)
    {
        var cutoff = now.Date.AddDays(-(KeepDays - 1)).ToString(DayFormat, CultureInfo.InvariantCulture);
        foreach (var url in _days.Keys.ToList())
        {
            var days = _days[url];
            foreach (var day in days.Keys.TakeWhile(d => string.CompareOrdinal(d, cutoff) < 0).ToList()) days.Remove(day);
            if (days.Count == 0) _days.Remove(url);
        }
    }

    private void SaveLocked()
    {
        if (!_dirty || _file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            AtomicFile.WriteAllText(_file, Serialize());
            _dirty = false;
            _lastSave = _now();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht fatal: der Zähler bleibt im Speicher, der nächste Versuch kommt mit der nächsten Anfrage
            AppLog.Write($"cloud usage not saved: {ex.GetType().Name}");
        }
    }

    private string Serialize()
    {
        var providers = new JsonObject();
        foreach (var url in _days.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var days = new JsonObject();
            foreach (var (day, c) in _days[url])
                days[day] = new JsonObject { ["requests"] = c.Requests, ["errors"] = c.Errors, ["outTokens"] = c.OutTokens };
            providers[url] = days;
        }
        var root = new JsonObject { ["version"] = Version, ["providers"] = providers };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void Load()
    {
        if (_file == null || !File.Exists(_file)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_file)) is not JsonObject root || root["providers"] is not JsonObject providers) return;
            foreach (var (url, daysNode) in providers)
            {
                if (daysNode is not JsonObject dayObjects || url.Length == 0) continue;
                var days = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
                foreach (var (day, node) in dayObjects)
                {
                    if (node is not JsonObject o || !DateTime.TryParseExact(day, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
                    days[day] = new Counts { Requests = Num(o, "requests"), Errors = Num(o, "errors"), OutTokens = Num(o, "outTokens") };
                }
                if (days.Count > 0) _days[url] = days;
            }
            Prune(_now());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Beschädigte Datei: ohne Verlauf weiter, die Datei wird beim nächsten Speichern ersetzt
            _days.Clear();
        }
    }

    private static long Num(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out long n) ? Math.Max(0, n) : 0;
}
