using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace StykkerLlm.Core;

// Fehlerbericht: ein Zip im Datenordner (bug-reports\) mit Beschreibung, Umgebung, den Protokollen aller Programme,
// den Einstellungen und – wenn der Server läuft – seinem Zustand. Nie dabei: Schlüssel, Zugangscode, Geräte,
// Anbieter-Schlüssel (access.dat, server.key, hosts.dat, host.json, providers\, confirm.key, web-shell.dat). Was mitgeht, wird
// geschwärzt: Werte von --api-key/--hf-token & Co., Tokens in URLs, der Windows-Benutzername und der Rechnername.
// Dazu eine Adresse für ein neues GitHub-Issue mit Beschreibung und Umgebung (ohne Protokolle – das Zip hängt man an).
public static partial class BugReport
{
    public const string IssueBase = "https://github.com/CrimsonED1/Stykker-LLM/issues/new";
    public const long MaxLogBytes = 512 * 1024;     // je Datei nur das Ende

    public sealed record Result(string ZipPath, IReadOnlyList<string> Entries, string IssueUrl, string Summary);

    public static Result Create(AppPaths paths, string description, string? stateJson, DateTime now, string? appDir = null,
        string? gpu = null)
    {
        var dir = Path.Combine(paths.Root, "bug-reports");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"stykker-bugreport-{now:yyyyMMdd-HHmmss}.zip");
        var entries = new List<string>();
        var env = Environment(gpu);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            Add(zip, entries, "report.txt", $"{description.Trim()}\n\n── Environment ──\n{env}\nCreated {now:yyyy-MM-dd HH:mm:ss}\n");
            foreach (var logDir in AppLog.Dirs(paths, appDir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(logDir)) continue;
                var tag = string.Equals(Path.GetFullPath(logDir).TrimEnd('\\', '/'), Path.GetFullPath(paths.LogsDir).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase) ? "data" : "app";
                foreach (var file in Directory.GetFiles(logDir, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = $"logs/{tag}/{Path.GetFileName(file)}";
                    if (entries.Contains(name)) continue;
                    Add(zip, entries, name, Redact(Tail(file)));
                }
            }
            if (File.Exists(paths.SettingsFile)) Add(zip, entries, "settings.json", Redact(ReadShared(paths.SettingsFile)));
            if (!string.IsNullOrWhiteSpace(stateJson)) Add(zip, entries, "state.json", Redact(stateJson));
        }
        var summary = Strings.BugReportSummary(zipPath, entries.Count);
        return new Result(zipPath, entries, IssueUrl(description, env, Path.GetFileName(zipPath)), summary);
    }

    // Neues Issue, vorausgefüllt: Titel aus der ersten Zeile, Text mit Beschreibung und Umgebung
    public static string IssueUrl(string description, string env, string zipName)
    {
        var text = description.Trim();
        var first = text.Split('\n')[0].Trim();
        var title = first.Length == 0 ? "Bug report" : first.Length > 80 ? first[..80] : first;
        if (text.Length > 3000) text = text[..3000] + " …";
        var body = $"{text}\n\n### Environment\n```\n{env}```\n\nReport file: `{zipName}` (please attach it here; it contains logs, " +
                   "settings and state with secrets removed).";
        return $"{IssueBase}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

    public static string Environment(string? gpu = null)
    {
        var sb = new StringBuilder();
        sb.Append("StykkerLLM ").Append(AppLog.Version()).Append('\n');
        sb.Append("OS ").Append(System.Runtime.InteropServices.RuntimeInformation.OSDescription)
          .Append(' ').Append(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture).Append('\n');
        sb.Append(".NET ").Append(System.Environment.Version).Append(" · ").Append(System.Environment.ProcessorCount).Append(" threads\n");
        if (!string.IsNullOrWhiteSpace(gpu)) sb.Append("GPU ").Append(gpu).Append('\n');
        return sb.ToString();
    }

    // Geheimnisse und Personenbezug schwärzen
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = SecretArg().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + CmdLine.Redacted);
        text = SecretQuery().Replace(text, m => m.Groups[1].Value + CmdLine.Redacted);
        text = SecretJson().Replace(text, m => m.Groups[1].Value + CmdLine.Redacted + "\"");
        text = Bearer().Replace(text, "Bearer " + CmdLine.Redacted);
        if (System.Environment.MachineName.Length >= 3)
            text = Regex.Replace(text, Regex.Escape(System.Environment.MachineName), "<pc>", RegexOptions.IgnoreCase);
        if (System.Environment.UserName.Length >= 3)
            text = Regex.Replace(text, @"\b" + Regex.Escape(System.Environment.UserName) + @"\b", "<user>", RegexOptions.IgnoreCase);
        return text;
    }

    [GeneratedRegex(@"(--(?:api-key|hf-token|token|password|api_key|key)|-hft)(\s+|=)(""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretArg();
    [GeneratedRegex(@"([?&](?:code|token|key|api_key)=)[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQuery();
    [GeneratedRegex(@"(""(?:[A-Za-z]*(?:Key|Token|Secret|Password|Code))""\s*:\s*"")[^""]*""", RegexOptions.None)]
    private static partial Regex SecretJson();
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    private static void Add(ZipArchive zip, List<string> entries, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text);
        entries.Add(name);
    }

    // Andere Programme schreiben vielleicht gerade hinein: lesend mit geteiltem Zugriff
    private static string ReadShared(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"(not readable: {ex.Message})"; }
    }

    private static string Tail(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxLogBytes) fs.Seek(-MaxLogBytes, SeekOrigin.End);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"(not readable: {ex.Message})"; }
    }
}
