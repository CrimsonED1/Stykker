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

// Modelltest der Sicherheitsprüfung (Modus „auto“ von Claude Code): je Modell eines Cloud-Anbieters eine Prüfung schicken und
// messen, ob es schnell genug und im richtigen Format antwortet. Nacheinander mit Pause; jede Zeile wird sofort an
// classifier-test.csv angehängt. Abbruch lässt die bis dahin gemessenen Zeilen stehen.
public sealed class ClassifierTest
{
    public const string CsvFile = "classifier-test.csv";
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
    private static readonly Dictionary<string, object> CsvGates = new();

    private readonly string _url;
    private readonly string[] _models;
    private readonly byte[] _request;
    private readonly bool _real;
    private readonly ProviderKeys? _keys;
    private readonly HttpClient _http;
    private readonly string? _csvPath;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _timeout, _pause, _retryWait;

    private readonly object _gate = new();
    private readonly List<ClassifierResult> _results = new();
    private volatile bool _running;
    private volatile int _done, _total;
    private volatile string _current = "";
    private volatile string _error = "";

    public ClassifierTest(string providerUrl, IEnumerable<string> models, ClassifierRequest request, ProviderKeys? keys, HttpClient http,
        string? csvPath = null, Func<DateTime>? now = null, TimeSpan? timeout = null, TimeSpan? pause = null, TimeSpan? retryWait = null)
    {
        _url = ProviderKeys.Normalize(providerUrl);
        _models = models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        _request = request.Body;
        _real = request.Real;
        _keys = keys;
        _http = http;
        _csvPath = string.IsNullOrWhiteSpace(csvPath) ? null : csvPath;
        _now = now ?? (() => DateTime.Now);
        _timeout = timeout ?? DefaultTimeout;
        _pause = pause ?? DefaultPause;
        _retryWait = retryWait ?? DefaultRetryWait;
    }

    public string ProviderUrl => _url;
    // Ist die Testanfrage die letzte echte Prüfung (mit dem Gesprächsinhalt)?
    public bool RequestIsReal => _real;
    public bool Running => _running;
    public int Done => _done;
    public int Total => _total;
    // Das Modell, das gerade dran ist („" wenn keins)
    public string Current => _current;
    // Warum nichts lief (z. B. kein Schlüssel); sonst leer
    public string Error => _error;

    // Die bisherigen Ergebnisse in der Reihenfolge des Laufs (eine Kopie, sicher aus der Oberfläche zu lesen)
    public IReadOnlyList<ClassifierResult> Results { get { lock (_gate) return _results.ToList(); } }

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
            var key = _keys?.Get(_url);
            if (string.IsNullOrEmpty(key)) { _error = Strings.ProxyProviderKeyMissingShort; return; }
            bool sent = false;
            foreach (var model in _models)
            {
                ct.ThrowIfCancellationRequested();
                _current = model;
                if (SkipWord(model) is { } word)
                    Add(new ClassifierResult(_now(), model, Strings.ClassifierStatusSkipped, null, null, null, Strings.ClassifierNoteSkipped(word)));
                else
                {
                    if (sent) await Task.Delay(_pause, ct).ConfigureAwait(false);
                    sent = true;
                    Add(await MeasureAsync(model, key, ct).ConfigureAwait(false));
                }
                _done++;
            }
        }
        catch (OperationCanceledException) { /* abgebrochen: was gemessen ist, bleibt */ }
        finally
        {
            _current = "";
            _running = false;
        }
    }

    // Eine Anfrage; bei 429 einmal nach der Wartezeit wiederholen
    private async Task<ClassifierResult> MeasureAsync(string model, string key, CancellationToken ct)
    {
        var first = await SendAsync(model, key, ct).ConfigureAwait(false);
        if (first.Status != "429") return first;
        await Task.Delay(_retryWait, ct).ConfigureAwait(false);
        var again = await SendAsync(model, key, ct).ConfigureAwait(false);
        var note = Strings.ClassifierNoteRetried(_retryWait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
        return again with { Note = again.Note.Length > 0 ? note + " " + again.Note : note };
    }

    private async Task<ClassifierResult> SendAsync(string model, string key, CancellationToken ct)
    {
        // Wie Claude Code mit Denken aus: die Prüfung braucht nur ein kurzes Urteil
        var body = AnthropicBridge.ToOpenAi(_request, model, includeUsage: false, noThinking: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, NetAddr.JoinUpstream(_url, "/v1/chat/completions")) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var when = _now();
        var clock = Stopwatch.StartNew();
        double? firstByte = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            firstByte = clock.Elapsed.TotalSeconds;
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
        // Eine Sperre je Datei, damit zwei Läufe nicht durcheinander schreiben
        object gate;
        lock (CsvGates)
        {
            var name = Path.GetFullPath(_csvPath);
            if (!CsvGates.TryGetValue(name, out var found)) CsvGates[name] = found = new object();
            gate = found;
        }
        lock (gate)
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

    private static string Csv(string v) =>
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
