using System.Text;

namespace StykkerLlm.Core;

// Ein gemerktes Profil als Startskript (PowerShell oder Batch). Geschwärzte Geheimnisse ("***") bleiben geschwärzt –
// das Skript weist darauf hin, statt einen Wert zu erfinden.
public static class ScriptExport
{
    // Download im Profilfenster (Idee B2): „ps1“ oder „cmd“, Dateiname aus dem Profilnamen. Argumente werden noch einmal
    // geschwärzt (ein von Hand eingetragener Schlüssel soll nie in einer Datei landen). null = unbekannte Art.
    public static (string FileName, string Text)? ForProfile(Profile p, string? kind) =>
        For(p.Name, p.Program, p.Args, p.WorkingDir, p.Env, kind);

    // Dasselbe für einen Eintrag des Verlaufs (Seite /history/{key}): was lief, lässt sich ohne Merken als Skript mitnehmen
    public static (string FileName, string Text)? ForHistory(HistoryEntry h, string? kind) =>
        For(h.Name, h.Program, h.Args, h.WorkingDir, h.Env, kind);

    private static (string FileName, string Text)? For(string name, string program, IReadOnlyList<string> rawArgs, string? workingDir,
        IReadOnlyDictionary<string, string> env, string? kind)
    {
        var (args, _) = CmdLine.Redact(rawArgs);
        var title = name.Length > 0 ? name : null;
        var stem = FileStem(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(program));
        return (kind ?? "ps1").ToLowerInvariant() switch
        {
            "ps1" => (stem + ".ps1", ToPowerShell(program, args, workingDir, env, title)),
            "cmd" => (stem + ".cmd", ToCmd(program, args, workingDir, env, title)),
            _ => null,
        };
    }

    // „qwen3 8B / q4“ → „start-qwen3-8B-q4“: nur Buchstaben, Ziffern, - _ . (für jedes Dateisystem und jeden Browser)
    public static string FileStem(string name)
    {
        var sb = new StringBuilder("start-");
        foreach (var c in name.Trim())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        var stem = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries)).Trim('.');
        return stem.Length > 64 ? stem[..64] : stem.Length > "start".Length ? stem : "start-server";
    }

    public static string ToPowerShell(string program, IReadOnlyList<string> args, string? workingDir, IReadOnlyDictionary<string, string>? env, string? title = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Start script for {title ?? Path.GetFileNameWithoutExtension(program)} (exported from StykkerLLM)");
        if (HasRedacted(args, env)) sb.AppendLine("# NOTE: values shown as *** were redacted (API keys/tokens) – fill them in before running.");
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
            sb.AppendLine($"$env:{k} = {Ps(v)}");
        if (!string.IsNullOrEmpty(workingDir)) sb.AppendLine($"Set-Location {Ps(workingDir)}");
        sb.Append("& ").Append(Ps(program));
        foreach (var a in args) sb.Append(' ').Append(Ps(a));
        sb.AppendLine();
        return sb.ToString();
    }

    public static string ToCmd(string program, IReadOnlyList<string> args, string? workingDir, IReadOnlyDictionary<string, string>? env, string? title = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"rem Start script for {title ?? Path.GetFileNameWithoutExtension(program)} (exported from StykkerLLM)");
        if (HasRedacted(args, env)) sb.AppendLine("rem NOTE: values shown as *** were redacted (API keys/tokens) - fill them in before running.");
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
            sb.AppendLine($"set \"{k}={Bat(v)}\"");
        if (!string.IsNullOrEmpty(workingDir)) sb.AppendLine($"cd /d {CmdQuote(workingDir)}");
        sb.Append(CmdQuote(program));
        foreach (var a in args) sb.Append(' ').Append(CmdQuote(a));
        sb.AppendLine();
        return sb.ToString();
    }

    private static bool HasRedacted(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env) =>
        args.Any(a => a.Contains(CmdLine.Redacted)) || (env?.Values.Any(v => v.Contains(CmdLine.Redacted)) ?? false);

    // PowerShell: einfache Anführungszeichen, ' wird verdoppelt (keine Variablen-Expansion)
    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    // Batch: % muss in .bat-Dateien verdoppelt werden
    private static string Bat(string s) => s.Replace("%", "%%");

    private static string CmdQuote(string s)
    {
        var b = Bat(s);
        return b.Length > 0 && b.IndexOfAny(new[] { ' ', '\t', '&', '|', '<', '>', '^', '(', ')' }) < 0 && !b.Contains('"') ? b : "\"" + b.Replace("\"", "\\\"") + "\"";
    }
}
