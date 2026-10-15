using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StykkerLlm.Core;

// Ein Ergebnis je Modell: Status (HTTP-Code, „timeout“, „error“ oder „skipped“), Zeit bis zur vollständigen Antwort, ob das Modell
// das Bild richtig beschrieben hat (Ok) und eine kurze Notiz. Nie der Schlüssel.
public sealed record VisionResult(DateTime Time, string Model, string Status, double? TotalSec, bool Ok, string Note);

// Modelltest für Bilder („Test vision models“ im Proxy-Dialog): je Modell eines Cloud-Anbieters eine kleine Anfrage mit einem
// eingebauten Bild schicken. Ok = Status 200 und die Antwort nennt „red“ und „blue“ (das Bild: links rot, rechts blau).
// Aufbau wie der Modelltest der Sicherheitsprüfung (ClassifierTest): gleiche Reihenfolge, gleiche Überspring-Regeln und Liste der
// nicht verfügbaren Modelle, Pausen, eine Wiederholung bei 429, und jede Zeile sofort an vision-test.csv angehängt.
public sealed class VisionTest
{
    public const string CsvFile = "vision-test.csv";
    // Kopfzeile der CSV (Datenformat, kein Anzeigetext)
    internal const string CsvHeader = "Time,Provider,Model,Status,TotalSec,Ok,Note";
    // Die Frage zum Bild (englisch: die Modelle antworten so zuverlässig)
    public const string Question = "Which two colours do you see? Answer with two words.";
    // Bildmaße der eingebauten Prüfung: links Rot, rechts Blau
    public const int ImageSize = 64;

    private static readonly Regex RedWord = new(@"\bred\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlueWord = new(@"\bblue\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _url;
    private readonly string[] _models;
    private readonly ProviderKeys? _keys;
    private readonly HttpClient _http;
    private readonly string? _csvPath;
    private readonly string? _deadPath;
    private readonly bool _retryUnavailable;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _timeout, _pause, _retryWait;
    private readonly byte[] _png = TestImage();

    private readonly object _gate = new();
    private readonly List<VisionResult> _results = new();
    private volatile bool _running;
    private volatile int _done, _total;
    private volatile string _error = "";

    // offered: die beim Anbieter angehakten Modelle (sie laufen zuerst); deadPath: Datei der nicht verfügbaren Modelle (dieselbe wie
    // beim Klassifizierer); retryUnavailable: die Liste für diesen Lauf ignorieren („Retry unavailable“)
    public VisionTest(string providerUrl, IEnumerable<string> models, ProviderKeys? keys, HttpClient http,
        string? csvPath = null, Func<DateTime>? now = null, TimeSpan? timeout = null, TimeSpan? pause = null, TimeSpan? retryWait = null,
        IEnumerable<string>? offered = null, string? deadPath = null, bool retryUnavailable = false)
    {
        _url = ProviderKeys.Normalize(providerUrl);
        _models = ClassifierTest.RunOrder(models, offered ?? Array.Empty<string>());
        _keys = keys;
        _http = http;
        _csvPath = string.IsNullOrWhiteSpace(csvPath) ? null : csvPath;
        _deadPath = string.IsNullOrWhiteSpace(deadPath) ? null : deadPath;
        _retryUnavailable = retryUnavailable;
        _now = now ?? (() => DateTime.Now);
        _timeout = timeout ?? ClassifierTest.DefaultTimeout;
        _pause = pause ?? ClassifierTest.DefaultPause;
        _retryWait = retryWait ?? ClassifierTest.DefaultRetryWait;
        Status = new TestRunStatus(_now);
    }

    public string ProviderUrl => _url;
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
    public IReadOnlyList<VisionResult> Results { get { lock (_gate) return _results.ToList(); } }

    // Zähler für die Live-Zeile: ok = richtig beschrieben, skipped = nicht gefragt, failed = gefragt, aber nicht richtig
    public (int Ok, int Failed, int Skipped) Counts
    {
        get
        {
            var rows = Results;
            int ok = rows.Count(r => r.Ok);
            int skipped = rows.Count(r => r.Status == Strings.ClassifierStatusSkipped);
            return (ok, rows.Count - ok - skipped, skipped);
        }
    }

    // Grobe Restzeit in Sekunden (null, solange noch keine Anfrage gemessen ist)
    public double? RemainingSec() => Status.RemainingSec(
        Results.Where(r => r.Status != Strings.ClassifierStatusSkipped).Select(r => r.TotalSec), _pause.TotalSeconds);

    // Die Tabelle: brauchbare zuerst (schnellste oben), dann der Rest, übersprungene ganz unten
    public static IReadOnlyList<VisionResult> Sorted(IEnumerable<VisionResult> rows) =>
        rows.OrderBy(r => r.Ok ? 0 : r.Status == Strings.ClassifierStatusSkipped ? 2 : 1)
            .ThenBy(r => r.TotalSec ?? double.MaxValue)
            .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Ist die Antwort eine richtige Beschreibung des Bildes? Beide Farben müssen vorkommen (ganze Wörter, ohne Groß-/Kleinschreibung)
    internal static bool Describes(string reply) => RedWord.IsMatch(reply) && BlueWord.IsMatch(reply);

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
            // Was beim letzten Lauf mit 404 abgelehnt wurde, wird übersprungen – außer bei „Retry unavailable“
            var unavailable = _retryUnavailable ? new HashSet<string>(StringComparer.Ordinal) : ClassifierTest.DeadModels(_deadPath, _url);
            Status.Begin(_models.Count(m => ClassifierTest.SkipWord(m) == null && !unavailable.Contains(m)));
            bool sent = false;
            foreach (var model in _models)
            {
                ct.ThrowIfCancellationRequested();
                Status.Next(model);
                if (ClassifierTest.SkipWord(model) is { } word)
                    Add(new VisionResult(_now(), model, Strings.ClassifierStatusSkipped, null, false, Strings.ClassifierNoteSkipped(word)));
                else if (unavailable.Contains(model))
                    Add(new VisionResult(_now(), model, Strings.ClassifierStatusSkipped, null, false, Strings.ClassifierNoteUnavailableLast));
                else
                {
                    if (sent) { Status.Enter(TestPhase.Pause); await Task.Delay(_pause, ct).ConfigureAwait(false); }
                    sent = true;
                    Status.Sent();
                    var row = await MeasureAsync(model, key, ct).ConfigureAwait(false);
                    Add(row);
                    Status.Enter(TestPhase.Idle);
                    if (ClassifierTest.IsGone(row.Status, row.Note)) ClassifierTest.MarkDead(_deadPath, _url, model);
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
    private async Task<VisionResult> MeasureAsync(string model, string key, CancellationToken ct)
    {
        var first = await SendAsync(model, key, ct).ConfigureAwait(false);
        if (first.Status != "429") return first;
        Status.Enter(TestPhase.RateLimitWait, _retryWait.TotalSeconds);
        await Task.Delay(_retryWait, ct).ConfigureAwait(false);
        var again = await SendAsync(model, key, ct).ConfigureAwait(false);
        var note = Strings.ClassifierNoteRetried(_retryWait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
        return again with { Note = again.Note.Length > 0 ? note + " " + again.Note : note };
    }

    private async Task<VisionResult> SendAsync(string model, string key, CancellationToken ct)
    {
        var body = RequestBody(model, _png);
        using var req = new HttpRequestMessage(HttpMethod.Post, NetAddr.JoinUpstream(_url, "/v1/chat/completions")) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var when = _now();
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        Status.Enter(TestPhase.Sending);
        try
        {
            Status.Enter(TestPhase.WaitingFirstByte);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            Status.Enter(TestPhase.Receiving);
            var text = await resp.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            double total = clock.Elapsed.TotalSeconds;
            string status = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
            if (!resp.IsSuccessStatusCode)
                return new VisionResult(when, model, status, total, false, ClassifierTest.Redact(ClassifierTest.ErrorMessage(text) ?? resp.ReasonPhrase ?? "", key));
            var reply = ClassifierTest.ReplyText(text);
            if (reply == null) return new VisionResult(when, model, status, total, false, Strings.ClassifierNoteNotJson);
            bool ok = status == "200" && Describes(reply);
            return new VisionResult(when, model, status, total, ok,
                ok ? "" : reply.Trim().Length == 0 ? Strings.ClassifierNoteEmpty : Strings.VisionNoteWrong(ClassifierTest.Redact(ClassifierTest.Shorten(reply), key)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VisionResult(when, model, Strings.ClassifierStatusTimeout, clock.Elapsed.TotalSeconds, false,
                Strings.ClassifierNoteTimeout(_timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VisionResult(when, model, Strings.ClassifierStatusError, null, false,
                Strings.ClassifierNoteError(ClassifierTest.Redact(ClassifierTest.Shorten(ex.Message), key)));
        }
    }

    // Die Anfrage in der Form des Anbieters (Chat-API mit Bild als Datenadresse); ohne Gesprächsinhalt
    public static byte[] RequestBody(string model, byte[] png)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 200,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) } },
                        new JsonObject { ["type"] = "text", ["text"] = Question },
                    },
                },
            },
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    private void Add(VisionResult r)
    {
        lock (_gate) _results.Add(r);
        AppendCsv(r);
    }

    // Eine Zeile an vision-test.csv anhängen (Kopfzeile beim ersten Mal); gesperrt oder fehlend: die Zeile fällt weg
    private void AppendCsv(VisionResult r)
    {
        if (_csvPath == null) return;
        var line = string.Join(",",
            r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ClassifierTest.Csv(_url), ClassifierTest.Csv(r.Model), ClassifierTest.Csv(r.Status),
            r.TotalSec is double t ? t.ToString("0.00", CultureInfo.InvariantCulture) : "",
            r.Ok ? "yes" : "no",
            ClassifierTest.Csv(r.Note));
        lock (ClassifierTest.GateFor(_csvPath))
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

    // Das eingebaute Testbild: 64 × 64, links die Hälfte Rot, rechts Blau. Im Code erzeugt, ohne Datei und ohne Zusatzpakete.
    public static byte[] TestImage()
    {
        int n = ImageSize, half = n / 2;
        var raw = new MemoryStream();
        for (int y = 0; y < n; y++)
        {
            raw.WriteByte(0);   // Filterart „keiner“ je Zeile
            for (int x = 0; x < n; x++)
            {
                bool red = x < half;
                raw.WriteByte(red ? (byte)255 : (byte)0);
                raw.WriteByte(0);
                raw.WriteByte(red ? (byte)0 : (byte)255);
            }
        }
        byte[] packed;
        using (var mem = new MemoryStream())
        {
            var pixels = raw.ToArray();
            using (var z = new ZLibStream(mem, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(pixels, 0, pixels.Length);
            packed = mem.ToArray();
        }
        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });   // PNG-Signatur
        var ihdr = new MemoryStream();
        WriteBe(ihdr, (uint)n);
        WriteBe(ihdr, (uint)n);
        ihdr.WriteByte(8);     // Bittiefe je Kanal
        ihdr.WriteByte(2);     // Farbtyp RGB
        ihdr.WriteByte(0);     // Kompression
        ihdr.WriteByte(0);     // Filter
        ihdr.WriteByte(0);     // kein Interlace
        WriteChunk(png, "IHDR", ihdr.ToArray());
        WriteChunk(png, "IDAT", packed);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data, int from, int count)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = from; i < from + count; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var body = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(body, 0);
        data.CopyTo(body, typeBytes.Length);
        WriteBe(s, (uint)data.Length);
        s.Write(body, 0, body.Length);
        WriteBe(s, Crc(body, 0, body.Length));
    }

    private static void WriteBe(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
