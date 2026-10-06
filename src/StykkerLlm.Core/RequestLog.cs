using System.Globalization;
using System.Text;

namespace StykkerLlm.Core;

// Hängt jede live mitbekommene, abgeschlossene Anfrage als CSV-Zeile an (Trennzeichen ",", Zahlen mit Punkt, UTF-8).
// Ist die Datei gesperrt (z. B. in Excel geöffnet), bleiben die Zeilen im Speicher und werden beim nächsten Flush()
// nachgeschrieben. Hat eine vorhandene Datei eine andere Kopfzeile (ältere Version), wird in eine Nachbardatei
// "<name>.v2.csv" geschrieben, damit nichts vermischt wird.
public sealed class RequestLog
{
    internal const string Header = "Time,Server,Model,Client,PromptTokens,PromptTokensPerSec,ReplyTokens,ReplyTokensPerSec,Seconds,Status";
    private const int MaxQueued = 5000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private string? _path;
    private bool _checked;
    private readonly Queue<string> _pending = new();

    public RequestLog(string? path) => _path = string.IsNullOrWhiteSpace(path) ? null : path;

    public bool Enabled => _path != null;
    public int Pending => _pending.Count;
    public string? Path => _path;
    // Ab dieser Dateigröße wird das Protokoll nach "<name>.1.csv" weggeschoben (die vorherige .1-Datei wird ersetzt) und neu begonnen
    public long MaxBytes { get; set; } = 50L * 1024 * 1024;

    private void RotateIfTooBig()
    {
        try
        {
            if (_path == null || !File.Exists(_path) || new FileInfo(_path).Length <= MaxBytes) return;
            var old = System.IO.Path.ChangeExtension(_path, ".1.csv");
            if (File.Exists(old)) File.Delete(old);
            File.Move(_path, old);
        }
        catch { /* gesperrt: beim nächsten Flush noch einmal */ }
    }

    public void Append(FinishedRequest f)
    {
        if (_path == null || f.Seen == null) return;   // Einträge aus dem Start-Log nie protokollieren
        var line = string.Join(',',
            f.Seen.Value.ToString("yyyy-MM-dd HH:mm:ss", Inv), Q(f.Server), Q(f.Model), Q(f.Client),
            f.PromptTokens.ToString(Inv), f.PromptTps.ToString("0.0", Inv),
            f.GenTokens.ToString(Inv), f.GenTps.ToString("0.0", Inv), f.Seconds.ToString("0.0", Inv), StatusText(f.Status));
        lock (_pending)
        {
            if (_pending.Count >= MaxQueued) _pending.Dequeue();   // nur bei dauerhaft gesperrter Datei
            _pending.Enqueue(line);
        }
        Flush();
    }

    internal static string StatusText(ReqStatus s) => s switch { ReqStatus.Truncated => "truncated", ReqStatus.Cancelled => "cancelled", _ => "done" };

    // Schreibt alles Ausstehende; bei gesperrter Datei bleibt es liegen und wird später erneut versucht
    public void Flush()
    {
        if (_path == null) return;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!_checked) { _path = CompatiblePath(_path); _checked = true; }
                RotateIfTooBig();
                var sb = new StringBuilder();
                bool fresh = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                if (fresh) sb.Append(Header).Append("\r\n");
                foreach (var l in _pending) sb.Append(l).Append("\r\n");
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var w = new StreamWriter(fs, new UTF8Encoding(fresh)))   // BOM nur am Dateianfang
                    w.Write(sb.ToString());
                _pending.Clear();
            }
            catch { /* gesperrt oder Laufwerk weg: beim nächsten Flush noch einmal */ }
        }
    }

    // Vorhandene Datei mit fremder Kopfzeile nicht erweitern, sondern eine Nachbardatei verwenden
    private static string CompatiblePath(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return path;
        string first;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(fs, Encoding.UTF8, true)) first = r.ReadLine() ?? "";
        if (first.TrimStart('﻿') == Header) return path;
        return System.IO.Path.ChangeExtension(path, ".v2.csv");
    }

    private static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
