using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Ein Ergebnis je Modell: Status (HTTP-Code, „timeout“, „error“ oder „skipped“), Zeit bis zum ersten Byte und bis zur letzten,
// ob die Antwort das Format der Prüfung hat (null = nicht geprüft), und eine kurze Notiz. Nie der Schlüssel.
public sealed record ClassifierResult(DateTime Time, string Model, string Status, double? FirstByteSec, double? TotalSec, bool? FormatOk, string Note);

// Die Testanfrage in Anthropic-Form. Real = die letzte echte Sicherheitsprüfung aus proxy-debug, sonst die eingebaute Beispielanfrage.
public sealed record ClassifierRequest(byte[] Body, bool Real);

// Die Phase des Modelltests für die Live-Zeile im Proxy-Dialog; Idle = gerade keine Anfrage offen (Überspringen, Ende)
public enum TestPhase { Idle, Sending, WaitingFirstByte, Receiving, RateLimitWait, Pause }

// Der Lauf-Zustand eines Modelltests (Sicherheitsprüfung und Bilder gleich): Phase seit wann, das Modell, bei 429 die Wartezeit,
// wie viele Anfragen noch gehen. Die Uhr kommt von außen (Tests). Die Oberfläche liest nur; geschrieben wird aus der Klasse des Laufs.
public sealed class TestRunStatus
{
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private TestPhase _phase;
    private DateTime _phaseSince, _requestSince;
    private string _model = "";
    private double _waitSec;
    private int _toSend;

    public TestRunStatus(Func<DateTime> now) => _now = now;

    public TestPhase Phase { get { lock (_gate) return _phase; } }
    // Das Modell, das gerade dran ist („" wenn keins)
    public string Model { get { lock (_gate) return _model; } }
    // Sekunden in der aktuellen Phase (0 bei Idle)
    public double PhaseSeconds { get { lock (_gate) return _phase == TestPhase.Idle ? 0 : (_now() - _phaseSince).TotalSeconds; } }
    // Die Wartezeit der Phase „RateLimitWait“ in Sekunden
    public double WaitSeconds { get { lock (_gate) return _waitSec; } }
    // Sekunden seit Beginn der laufenden Anfrage (0, wenn gerade keine offen ist: Pause, Wartezeit bei 429, Ende)
    public double RequestSeconds { get { lock (_gate) return InFlight ? (_now() - _requestSince).TotalSeconds : 0; } }

    // Ist gerade eine Anfrage offen? (Senden, Warten auf die erste Antwort, Lesen der Antwort)
    private bool InFlight => _phase is TestPhase.Sending or TestPhase.WaitingFirstByte or TestPhase.Receiving;

    // Ein Lauf beginnt: so viele Anfragen gehen noch raus (ohne die übersprungenen)
    internal void Begin(int toSend)
    {
        lock (_gate) { _phase = TestPhase.Idle; _phaseSince = _now(); _model = ""; _waitSec = 0; _toSend = Math.Max(0, toSend); }
    }

    // Das nächste Modell ist dran (auch wenn es übersprungen wird)
    internal void Next(string model)
    {
        lock (_gate) _model = model;
    }

    // Eine Anfrage geht raus: sie zählt nicht mehr zu den offenen
    internal void Sent()
    {
        lock (_gate) _toSend = Math.Max(0, _toSend - 1);
    }

    // Die Phase wechselt. Bei RateLimitWait zählt die Wartezeit; bei Sending beginnt die Zeit der Anfrage.
    internal void Enter(TestPhase phase, double waitSec = 0)
    {
        lock (_gate)
        {
            _phase = phase;
            _phaseSince = _now();
            if (phase == TestPhase.Sending) _requestSince = _phaseSince;
            if (phase == TestPhase.RateLimitWait) _waitSec = waitSec;
        }
    }

    // Der Lauf ist zu Ende (oder abgebrochen): nichts läuft mehr
    internal void End()
    {
        lock (_gate) { _phase = TestPhase.Idle; _phaseSince = _now(); _model = ""; _toSend = 0; }
    }

    // Grobe Restzeit in Sekunden: die laufende Anfrage (soweit sie schon Zeit hat) plus die noch offenen Anfragen mit dem
    // Durchschnitt der bisher gemessenen Zeiten und je einer Pause. Null, solange noch keine Anfrage gemessen ist.
    internal double? RemainingSec(IEnumerable<double?> measuredSec, double pauseSec)
    {
        var list = measuredSec.Where(v => v is double).Select(v => v!.Value).ToList();
        lock (_gate)
        {
            if (list.Count == 0) return null;
            double avg = list.Average();
            double current = InFlight ? Math.Max(0, avg - (_now() - _requestSince).TotalSeconds) : 0;
            return current + _toSend * (avg + pauseSec);
        }
    }
}

// Bekannte Fehler kurz übersetzt, für die Anzeige. Das Original bleibt in der CSV und im Tooltip. Null = keine Übersetzung.
public static class TestNotes
{
    public static string? Friendly(string status, string note, double timeoutSec)
    {
        if (status == Strings.ClassifierStatusTimeout) return Strings.ClassifierNoteSlow(timeoutSec.ToString("0.#", CultureInfo.InvariantCulture));
        if (status == "400" && note.Contains("multimodal", StringComparison.OrdinalIgnoreCase)) return Strings.ClassifierNoteNoImage;
        if (status == "503" && note.Contains("ResourceExhausted", StringComparison.OrdinalIgnoreCase)) return Strings.ClassifierNoteBusy;
        if (status == "404") return Strings.ClassifierNoteNotForAccount;
        return null;
    }
}

// Modelltest der Sicherheitsprüfung (Modus „auto“ von Claude Code): je Modell eines Cloud-Anbieters eine Prüfung schicken und
// messen, ob es schnell genug und im richtigen Format antwortet. Nacheinander mit Pause; jede Zeile wird sofort an
// classifier-test.csv angehängt. Abbruch lässt die bis dahin gemessenen Zeilen stehen.
public sealed class ClassifierTest
{
    public const string CsvFile = "classifier-test.csv";
    // Die Quelle „dieser PC“: die laufenden lokalen Server statt eines Cloud-Anbieters. Steht in der CSV in der Spalte Provider;
    // die Modellnamen sind dann die Auswahlwerte des Proxys („<Server-Key>|<Modell>“), Adresse und Modell je Ziel kommen mit.
    public const string LocalSource = "local";
    // Modelle, die der Anbieter mit 404 abgelehnt hat: je Zeile „<Anbieter-URL>|<Modell>“, beim nächsten Lauf übersprungen
    public const string DeadFile = "classifier-dead.txt";
    // Kopfzeile der CSV (Datenformat, kein Anzeigetext)
    internal const string CsvHeader = "Time,Provider,Model,Status,FirstByteSec,TotalSec,Format ok,Note";
    // Eine Antwort, die nicht in dieser Zeit vollständig kommt, gilt als Zeitüberschreitung (wie bei Claude Code)
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultRetryWait = TimeSpan.FromSeconds(20);
    // Modelle mit einem dieser Wörter im Namen werden nicht geprüft (Einbettung, Umsortierung, Schutzmodell, Sprache, Bild …).
    // „vl“ und „vision“ stehen absichtlich nicht hier: Modelle mit Bildeingang bleiben im Test.
    private static readonly string[] SkipWords =
    {
        "embed", "rerank", "guard", "safety", "reward", "parakeet", "whisper", "tts", "asr", "canary", "riva",
        "flux", "stable-diffusion", "sdxl", "clip", "nv-embed", "bge",
    };
    // Bekannte aktuelle Familien (Teil des Namens, ohne Groß-/Kleinschreibung). Ihre Reihenfolge in der Liste ist die Priorität.
    private static readonly string[] Families =
    {
        "nvidia/", "nemotron", "qwen", "llama-3.3", "llama-4", "mistral", "z-ai/", "glm", "deepseek-v", "gpt-oss", "kimi", "minimax", "gemma-3",
    };
    private static readonly Dictionary<string, object> FileGates = new();

    private readonly string _url;
    private readonly string[] _models;
    private readonly byte[] _request;
    private readonly bool _real;
    private readonly ProviderKeys? _keys;
    private readonly IReadOnlyDictionary<string, LocalTestTarget>? _local;
    private readonly HttpClient _http;
    private readonly string? _csvPath;
    private readonly string? _deadPath;
    private readonly bool _retryUnavailable;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _timeout, _pause, _retryWait;

    private readonly object _gate = new();
    private readonly List<ClassifierResult> _results = new();
    private volatile bool _running;
    private volatile int _done, _total;
    private volatile string _error = "";

    // offered: die beim Anbieter angehakten Modelle (sie laufen zuerst); deadPath: Datei der nicht verfügbaren Modelle;
    // retryUnavailable: die Liste für diesen Lauf ignorieren („Retry unavailable“)
    public ClassifierTest(string providerUrl, IEnumerable<string> models, ClassifierRequest request, ProviderKeys? keys, HttpClient http,
        string? csvPath = null, Func<DateTime>? now = null, TimeSpan? timeout = null, TimeSpan? pause = null, TimeSpan? retryWait = null,
        IEnumerable<string>? offered = null, string? deadPath = null, bool retryUnavailable = false,
        IReadOnlyDictionary<string, LocalTestTarget>? local = null)
    {
        _url = ProviderKeys.Normalize(providerUrl);
        _models = RunOrder(models, offered ?? Array.Empty<string>());
        _request = request.Body;
        _real = request.Real;
        _keys = keys;
        _local = local;
        _http = http;
        _csvPath = string.IsNullOrWhiteSpace(csvPath) ? null : csvPath;
        _deadPath = string.IsNullOrWhiteSpace(deadPath) ? null : deadPath;
        _retryUnavailable = retryUnavailable;
        _now = now ?? (() => DateTime.Now);
        _timeout = timeout ?? DefaultTimeout;
        _pause = pause ?? DefaultPause;
        _retryWait = retryWait ?? DefaultRetryWait;
        Status = new TestRunStatus(_now);
    }

    public string ProviderUrl => _url;
    // Ist die Testanfrage die letzte echte Prüfung (mit dem Gesprächsinhalt)?
    public bool RequestIsReal => _real;
    public bool Running => _running;
    public int Done => _done;
    public int Total => _total;
    // Phase, Modell und Zeiten des Laufs für die Live-Zeile
    public TestRunStatus Status { get; }
    // Die Zeitgrenze je Anfrage in Sekunden (für die Live-Zeile und die Notiz „slower than …“)
    public double TimeoutSeconds => _timeout.TotalSeconds;
    // Das Modell, das gerade dran ist („" wenn keins)
    public string Current => Status.Model;
    // Warum nichts lief (z. B. kein Schlüssel); sonst leer
    public string Error => _error;

    // Die bisherigen Ergebnisse in der Reihenfolge des Laufs (eine Kopie, sicher aus der Oberfläche zu lesen)
    public IReadOnlyList<ClassifierResult> Results { get { lock (_gate) return _results.ToList(); } }

    // Zähler für die Live-Zeile: ok = Format stimmt, skipped = nicht gefragt, failed = gefragt, aber nicht brauchbar
    public (int Ok, int Failed, int Skipped) Counts => CountOf(Results);

    public static (int Ok, int Failed, int Skipped) CountOf(IEnumerable<ClassifierResult> rows)
    {
        var list = rows.ToList();
        int ok = list.Count(r => r.FormatOk is true);
        int skipped = list.Count(r => r.Status == Strings.ClassifierStatusSkipped);
        return (ok, list.Count - ok - skipped, skipped);
    }

    // Grobe Restzeit in Sekunden (null, solange noch keine Anfrage gemessen ist)
    public double? RemainingSec() => Status.RemainingSec(
        Results.Where(r => r.Status != Strings.ClassifierStatusSkipped).Select(r => r.TotalSec), _pause.TotalSeconds);

    // Die Reihenfolge des Laufs: (1) die angehakten Modelle des Anbieters, (2) bekannte aktuelle Familien in der Reihenfolge der
    // Liste, (3) der Rest. Innerhalb einer Gruppe alphabetisch (in Gruppe 2 zuerst nach Familie). Doppelte fallen weg.
    public static string[] RunOrder(IEnumerable<string> listed, IEnumerable<string> offered)
    {
        var chosen = new HashSet<string>(offered.Select(m => (m ?? "").Trim()), StringComparer.Ordinal);
        return listed.Select(m => (m ?? "").Trim()).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal)
            .Select(m => (Model: m, Group: chosen.Contains(m) ? 0 : FamilyRank(m) >= 0 ? 1 : 2, Rank: FamilyRank(m)))
            .OrderBy(x => x.Group).ThenBy(x => x.Group == 1 ? x.Rank : 0).ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Model)
            .ToArray();
    }

    // Die Stelle der ersten passenden Familie in der Liste, sonst -1
    private static int FamilyRank(string model)
    {
        for (int i = 0; i < Families.Length; i++)
            if (model.Contains(Families[i], StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // Die Tabelle: brauchbare zuerst (schnellste oben), dann der Rest, übersprungene ganz unten
    public static IReadOnlyList<ClassifierResult> Sorted(IEnumerable<ClassifierResult> rows) =>
        rows.OrderBy(r => r.FormatOk is true ? 0 : r.FormatOk is false ? 1 : 2)
            .ThenBy(r => r.TotalSec ?? double.MaxValue)
            .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Das Wort, das einen Modellnamen zum Überspringen macht (ohne Groß-/Kleinschreibung), sonst null
    public static string? SkipWord(string model) => SkipWords.FirstOrDefault(w => model.Contains(w, StringComparison.OrdinalIgnoreCase));

    // Läuft einmal durch alle Modelle. Der Schlüssel kommt aus ProviderKeys und verlässt diese Klasse nur als Authorization-Kopf.
    public async Task RunAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _results.Clear();
        }
        _done = 0;
        _total = _models.Length;
        _error = "";
        try
        {
            // Lokale Server brauchen keinen Schlüssel
            var key = _local != null ? "" : _keys?.Get(_url);
            if (key == null || (_local == null && key.Length == 0)) { _error = Strings.ProxyProviderKeyMissingShort; return; }
            // Was beim letzten Lauf mit 404 abgelehnt wurde, wird übersprungen – außer bei „Retry unavailable“
            var unavailable = _retryUnavailable ? new HashSet<string>(StringComparer.Ordinal) : DeadModels(_deadPath, _url);
            Status.Begin(_models.Count(m => SkipWord(m) == null && !unavailable.Contains(m)));
            bool sent = false;
            foreach (var model in _models)
            {
                ct.ThrowIfCancellationRequested();
                Status.Next(model);
                if (SkipWord(model) is { } word)
                    Add(new ClassifierResult(_now(), model, Strings.ClassifierStatusSkipped, null, null, null, Strings.ClassifierNoteSkipped(word)));
                else if (unavailable.Contains(model))
                    Add(new ClassifierResult(_now(), model, Strings.ClassifierStatusSkipped, null, null, null, Strings.ClassifierNoteUnavailableLast));
                else
                {
                    if (sent) { Status.Enter(TestPhase.Pause); await Task.Delay(_pause, ct).ConfigureAwait(false); }
                    sent = true;
                    Status.Sent();
                    var row = await MeasureAsync(model, key, ct).ConfigureAwait(false);
                    Add(row);
                    Status.Enter(TestPhase.Idle);
                    if (IsGone(row)) MarkDead(_deadPath, _url, model);
                }
                _done++;
            }
        }
        catch (OperationCanceledException) { /* abgebrochen: was gemessen ist, bleibt */ }
        finally
        {
            Status.End();
            _running = false;
        }
    }

    // Eine Anfrage; bei 429 einmal nach der Wartezeit wiederholen
    private async Task<ClassifierResult> MeasureAsync(string model, string key, CancellationToken ct)
    {
        var first = await SendAsync(model, key, ct).ConfigureAwait(false);
        if (first.Status != "429") return first;
        Status.Enter(TestPhase.RateLimitWait, _retryWait.TotalSeconds);
        await Task.Delay(_retryWait, ct).ConfigureAwait(false);
        var again = await SendAsync(model, key, ct).ConfigureAwait(false);
        var note = Strings.ClassifierNoteRetried(_retryWait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
        return again with { Note = again.Note.Length > 0 ? note + " " + again.Note : note };
    }

    private async Task<ClassifierResult> SendAsync(string model, string key, CancellationToken ct)
    {
        // Wie Claude Code mit Denken aus: die Prüfung braucht nur ein kurzes Urteil
        var (url, name) = LocalTestTarget.Resolve(_local, _url, model);
        var body = AnthropicBridge.ToOpenAi(_request, name, includeUsage: false, noThinking: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, NetAddr.JoinUpstream(url, "/v1/chat/completions")) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var when = _now();
        var clock = Stopwatch.StartNew();
        double? firstByte = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        Status.Enter(TestPhase.Sending);
        try
        {
            Status.Enter(TestPhase.WaitingFirstByte);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            firstByte = clock.Elapsed.TotalSeconds;
            Status.Enter(TestPhase.Receiving);
            var text = await resp.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            double total = clock.Elapsed.TotalSeconds;
            string status = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
            if (!resp.IsSuccessStatusCode)
                return new ClassifierResult(when, model, status, firstByte, total, false,
                    Redact(ErrorMessage(text) ?? resp.ReasonPhrase ?? "", key));
            var reply = ReplyText(text);
            if (reply == null) return new ClassifierResult(when, model, status, firstByte, total, false, Strings.ClassifierNoteNotJson);
            bool ok = IsBlock(reply);
            return new ClassifierResult(when, model, status, firstByte, total, ok,
                ok ? "" : reply.Length == 0 ? Strings.ClassifierNoteEmpty : Strings.ClassifierNoteFormat(Redact(Shorten(reply), key)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ClassifierResult(when, model, Strings.ClassifierStatusTimeout, firstByte, clock.Elapsed.TotalSeconds, false,
                Strings.ClassifierNoteTimeout(_timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClassifierResult(when, model, Strings.ClassifierStatusError, firstByte, null, false,
                Strings.ClassifierNoteError(Redact(Shorten(ex.Message), key)));
        }
    }

    // Die Antwort beginnt mit <block> (führende Leerzeichen erlaubt). Gelesen wird nur der Antworttext, nie reasoning_content.
    internal static bool IsBlock(string reply) => reply.TrimStart().StartsWith("<block>", StringComparison.Ordinal);

    // Der Antworttext der ersten Auswahl (choices[0].message.content); null = keine JSON-Antwort einer Chat-API
    internal static string? ReplyText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array)
                return null;
            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return "";
            return message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
        }
        catch (JsonException) { return null; }
    }

    // Die Fehlermeldung des Anbieters ({"error":{"message":…}} oder {"error":"…"}), sonst der Anfang des Textes
    internal static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return Shorten(err.GetString() ?? "");
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return Shorten(m.GetString() ?? "");
            }
        }
        catch (JsonException) { }
        var raw = body.Trim();
        return raw.Length == 0 ? null : Shorten(raw);
    }

    // Höchstens 200 Zeichen, Zeilenenden als Leerzeichen
    internal static string Shorten(string s)
    {
        var one = s.ReplaceLineEndings(" ").Trim();
        return one.Length <= 200 ? one : one[..200];
    }

    // Der Schlüssel darf in keiner Notiz und keiner CSV-Zeile stehen, auch wenn ein Anbieter ihn zurückschreibt
    internal static string Redact(string s, string key) => string.IsNullOrEmpty(key) ? s : s.Replace(key, "***", StringComparison.Ordinal);

    // Der Anbieter kennt das Modell nicht: 404, auch mit „Function … Not found for account“ oder „404 page not found“ als Text
    internal static bool IsGone(ClassifierResult r) => IsGone(r.Status, r.Note);

    internal static bool IsGone(string status, string note) =>
        status == "404" || (status.Length == 3 && status[0] != '2'
            && (note.Contains("Not found for account", StringComparison.OrdinalIgnoreCase) || note.Contains("404 page not found", StringComparison.OrdinalIgnoreCase)));

    // Die Modelle eines Anbieters aus der Datei der nicht verfügbaren Modelle (fehlende oder unlesbare Datei = keine)
    internal static HashSet<string> DeadModels(string? path, string url)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (path == null) return set;
        string[] lines;
        try { lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return set; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            int bar = line.IndexOf('|');
            if (bar <= 0 || !line[..bar].Equals(url, StringComparison.OrdinalIgnoreCase)) continue;
            var model = line[(bar + 1)..].Trim();
            if (model.Length > 0) set.Add(model);
        }
        return set;
    }

    // Ein Modell in die Datei eintragen (je Anbieter und Modell nur einmal)
    internal static void MarkDead(string? path, string url, string model)
    {
        if (path == null) return;
        var line = url + "|" + model;
        lock (GateFor(path))
        {
            try
            {
                if (File.Exists(path) && File.ReadAllLines(path).Any(l => l.Trim().Equals(line, StringComparison.OrdinalIgnoreCase))) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gesperrt: dieser Eintrag fällt weg */ }
        }
    }

    // Eine Sperre je Datei, damit zwei Läufe nicht durcheinander schreiben
    internal static object GateFor(string path)
    {
        var name = Path.GetFullPath(path);
        lock (FileGates)
        {
            if (!FileGates.TryGetValue(name, out var found)) FileGates[name] = found = new object();
            return found;
        }
    }

    private void Add(ClassifierResult r)
    {
        lock (_gate) _results.Add(r);
        AppendCsv(r);
    }

    private void AppendCsv(ClassifierResult r)
    {
        if (_csvPath == null) return;
        var line = string.Join(",",
            r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Csv(_url), Csv(r.Model), Csv(r.Status),
            r.FirstByteSec is double f ? f.ToString("0.00", CultureInfo.InvariantCulture) : "",
            r.TotalSec is double t ? t.ToString("0.00", CultureInfo.InvariantCulture) : "",
            r.FormatOk is bool ok ? (ok ? "yes" : "no") : "",
            Csv(r.Note));
        lock (GateFor(_csvPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);
                bool fresh = !File.Exists(_csvPath);
                File.AppendAllText(_csvPath, (fresh ? CsvHeader + "\r\n" : "") + line + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gesperrt: diese Zeile fällt weg */ }
        }
    }

    internal static string Csv(string v) =>
        v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    // Die Testanfrage: die neueste Datei in proxy-debug (die Namen beginnen mit der Zeit), die eine Sicherheitsprüfung ist;
    // gibt es keine, die eingebaute Beispielanfrage gleicher Form
    public static ClassifierRequest PickRequest(string? debugDir)
    {
        if (debugDir != null && Directory.Exists(debugDir))
        {
            FileInfo[] files;
            try { files = new DirectoryInfo(debugDir).GetFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = Array.Empty<FileInfo>(); }
            foreach (var f in files)
            {
                try
                {
                    var bytes = File.ReadAllBytes(f.FullName);
                    if (RouterProxy.IsSafetyCheck(bytes)) return new ClassifierRequest(bytes, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return new ClassifierRequest(SampleBody(), false);
    }

    // Eingebaute Beispielanfrage, so gebaut wie die Prüfung: Fülltext (etwa 180 KB) im System-Text, „</block>“ als Stoppzeichen,
    // eine kurze Nutzer-Nachricht, keine Werkzeuge. Ohne Gesprächsinhalt.
    public static byte[] SampleBody()
    {
        var filler = string.Concat(Enumerable.Repeat("Example filler sentence for the safety check test. ", 3600));
        var body = new JsonObject
        {
            ["model"] = "claude-haiku-5-5",
            ["max_tokens"] = 2112,
            ["stop_sequences"] = new JsonArray { "</block>" },
            ["system"] = "You are a safety classifier. Answer with <block>yes</block> or <block>no</block>.\n" + filler,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Answer with exactly <block>no</block>." } },
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }
}

// Ein lokales Ziel eines Modelltests: Adresse des Servers und der Modellname, den er erwartet. Der Test kennt das Ziel unter dem
// Auswahlwert des Proxys; Resolve gibt für Cloud-Anbieter (local == null) die Anbieter-URL und den Namen unverändert zurück.
public sealed record LocalTestTarget(string Url, string Model)
{
    public static (string Url, string Model) Resolve(IReadOnlyDictionary<string, LocalTestTarget>? local, string providerUrl, string model) =>
        local != null && local.TryGetValue(model, out var t) ? (t.Url, t.Model) : (providerUrl, model);
}
