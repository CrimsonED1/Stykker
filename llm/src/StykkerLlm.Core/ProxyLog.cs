using System.Globalization;
using System.Text;

namespace StykkerLlm.Core;

// Protokoll des Stykker-Proxys (proxy.csv neben requests.csv): jede Anfrage mit dem Modellnamen, den der Client wollte,
// und dem Ziel, an das sie ging – auch Cloud-Ziele, die requests.csv (Slots der lokalen Server) nicht sieht.
// Nur Name, Ziel, Zeiten und Zahlen; nie der Inhalt der Anfrage.
public sealed class ProxyLog
{
    internal const string Header = "Time,Requested,Target,Path,Client,HttpStatus,Seconds,ReplyTokens,Aborted,MaxTokens,Messages,Tools";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly object _gate = new();
    private readonly string? _path;

    public ProxyLog(string? path) => _path = string.IsNullOrWhiteSpace(path) ? null : path;

    public string? Path => _path;
    public long MaxBytes { get; set; } = 50L * 1024 * 1024;

    public void Append(ProxyRecord r, string target)
    {
        if (_path == null) return;
        int reply = r.ReasoningTokens < 0 ? -1 : r.ReasoningTokens + r.ContentTokens;
        var line = string.Join(",",
            r.Start.ToString("yyyy-MM-dd HH:mm:ss", Inv), Csv(r.RequestedModel ?? ""), Csv(target), Csv(r.Path), Csv(r.UserAgent),
            r.HttpStatus.ToString(Inv), (r.End - r.Start).TotalSeconds.ToString("0.0", Inv), reply.ToString(Inv), r.ClientAborted ? "yes" : "no",
            (r.Shape ?? RequestShape.None).MaxTokens.ToString(Inv), (r.Shape ?? RequestShape.None).Messages.ToString(Inv), Csv((r.Shape ?? RequestShape.None).Tools));
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    var old = System.IO.Path.ChangeExtension(_path, ".1.csv");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                bool fresh = !File.Exists(_path);
                File.AppendAllText(_path, (fresh ? Header + "\r\n" : "") + line + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gesperrt: diese Zeile fällt weg */ }
        }
    }

    // Zur Fehlersuche: der volle Anfragetext als Datei „<Zeit>-<Modell>.json“; nur die letzten KeepDebug Dateien bleiben
    public const int KeepDebug = 100;

    public static void WriteDebug(string dir, ProxyRecord r, byte[] body)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var model = new string((r.RequestedModel ?? "none").Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());
            if (model.Length > 60) model = model[..60];
            File.WriteAllBytes(System.IO.Path.Combine(dir, $"{r.Start:yyyyMMdd-HHmmss-fff}-{model}.json"), body);
            var files = new DirectoryInfo(dir).GetFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepDebug);
            foreach (var f in files) f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Csv(string v) =>
        v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
