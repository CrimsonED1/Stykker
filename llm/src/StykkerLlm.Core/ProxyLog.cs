using System.Globalization;
using System.Text;

namespace StykkerLlm.Core;

// Protokoll des Stykker-Proxys (proxy.csv neben requests.csv): jede Anfrage mit dem Modellnamen, den der Client wollte,
// und dem Ziel, an das sie ging – auch Cloud-Ziele, die requests.csv (Slots der lokalen Server) nicht sieht.
// Nur Name, Ziel, Zeiten und Zahlen; nie der Inhalt der Anfrage.
public sealed class ProxyLog
{
    internal const string Header = "Time,Requested,Target,Path,Client,HttpStatus,Seconds,ReplyTokens,Aborted";
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
            r.HttpStatus.ToString(Inv), (r.End - r.Start).TotalSeconds.ToString("0.0", Inv), reply.ToString(Inv), r.ClientAborted ? "yes" : "no");
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

    private static string Csv(string v) =>
        v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
