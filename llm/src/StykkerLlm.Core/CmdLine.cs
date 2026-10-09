using System.Text;

namespace StykkerLlm.Core;

// Kommandozeilen lesen, zusammensetzen und Geheimnisse schwärzen.
public static class CmdLine
{
    public const string Redacted = "***";

    // Optionen, deren Wert ein Geheimnis oder ein Verweis darauf ist: nie im Klartext speichern, anzeigen oder kopieren
    private static readonly HashSet<string> SecretOptions = new(StringComparer.Ordinal)
    {
        "--api-key", "--api-key-file", "--hf-token", "-hft",
    };

    // Umgebungsvariablen, die llama.cpp und die GPU-Laufzeit lesen (nur diese werden überhaupt gemerkt)
    private static readonly string[] EnvPrefixes = { "GGML_", "LLAMA_ARG_", "HIP_" };

    public static bool IsEnvWhitelisted(string name) =>
        name.Equals("CUDA_VISIBLE_DEVICES", StringComparison.OrdinalIgnoreCase) ||
        EnvPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsSecretEnv(string name)
    {
        var n = name.ToUpperInvariant();
        return n.Contains("KEY") || n.Contains("TOKEN") || n.Contains("SECRET") || n.Contains("PASSWORD");
    }

    // ── Zerlegen nach den Regeln von CommandLineToArgvW ──
    // Das erste Element (Programmname) wird anders gelesen: in Anführungszeichen bis zum nächsten Anführungszeichen,
    // sonst bis zum ersten Leerzeichen, ohne Backslash-Regeln. Danach:
    //  2n Backslashes + " -> n Backslashes, das Anführungszeichen schaltet um; 2n+1 Backslashes + " -> n Backslashes und
    //  ein wörtliches "; Backslashes ohne folgendes " bleiben; in Anführungszeichen ergibt "" ein wörtliches ".
    public static List<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return result;
        const char Bs = '\\', Q = '"';
        int i = 0, n = commandLine.Length;

        // Programmname
        while (i < n && (commandLine[i] == ' ' || commandLine[i] == '\t')) i++;
        if (i >= n) return result;
        var prog = new StringBuilder();
        if (commandLine[i] == Q)
        {
            i++;
            while (i < n && commandLine[i] != Q) prog.Append(commandLine[i++]);
            if (i < n) i++;   // schließendes "
        }
        else
            while (i < n && commandLine[i] != ' ' && commandLine[i] != '\t') prog.Append(commandLine[i++]);
        result.Add(prog.ToString());

        // Argumente
        while (true)
        {
            while (i < n && (commandLine[i] == ' ' || commandLine[i] == '\t')) i++;
            if (i >= n) break;
            var arg = new StringBuilder();
            bool inQuote = false;
            while (i < n)
            {
                char c = commandLine[i];
                if (c == Bs)
                {
                    int bs = 0;
                    while (i < n && commandLine[i] == Bs) { bs++; i++; }
                    if (i < n && commandLine[i] == Q)
                    {
                        arg.Append(Bs, bs / 2);
                        if (bs % 2 == 1) { arg.Append(Q); i++; }
                        // gerade Anzahl: das " wird im nächsten Durchlauf als Umschalter gelesen
                    }
                    else arg.Append(Bs, bs);
                    continue;
                }
                if (c == Q)
                {
                    if (inQuote && i + 1 < n && commandLine[i + 1] == Q) { arg.Append(Q); i += 2; continue; }
                    inQuote = !inQuote; i++; continue;
                }
                if (!inQuote && (c == ' ' || c == '\t')) break;
                arg.Append(c); i++;
            }
            result.Add(arg.ToString());
        }
        return result;
    }

    // Ein Argument so in Anführungszeichen setzen, dass Split es wieder genau so liefert (Regeln wie ProcessStartInfo.ArgumentList)
    public static string Quote(string arg)
    {
        const char Bs = '\\', Q = '"';
        if (arg.Length == 0) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', Q }) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int bs = 0;
            while (i < arg.Length && arg[i] == Bs) { bs++; i++; }
            if (i == arg.Length) { sb.Append(Bs, bs * 2); break; }        // Backslashes vor dem schließenden "
            if (arg[i] == Q) sb.Append(Bs, bs * 2 + 1).Append(Q);          // Backslashes vor einem " verdoppeln, " maskieren
            else sb.Append(Bs, bs).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    // Schwärzt die Werte der Geheimnis-Optionen (getrennt "--api-key X" und "--api-key=X"). Args enthält kein Programm.
    public static (List<string> Args, bool HadSecrets) Redact(IReadOnlyList<string> args)
    {
        var result = new List<string>(args.Count);
        bool had = false;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            int eq = a.StartsWith("-", StringComparison.Ordinal) ? a.IndexOf('=') : -1;   // auch Kurzflags wie -hft=x
            if (eq > 0 && SecretOptions.Contains(a[..eq]))
            {
                result.Add(a[..eq] + "=" + Redacted);
                had = true;
            }
            else if (SecretOptions.Contains(a))
            {
                result.Add(a);
                if (i + 1 < args.Count) { result.Add(Redacted); i++; }
                had = true;
            }
            else result.Add(a);
        }
        return (result, had);
    }

    // Umgebung: nur Whitelist, Werte von Schlüssel-/Token-Variablen geschwärzt
    public static (Dictionary<string, string> Env, bool HadSecrets) FilterEnvironment(IEnumerable<KeyValuePair<string, string>> env)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool had = false;
        foreach (var (k, v) in env)
        {
            if (!IsEnvWhitelisted(k)) continue;
            if (IsSecretEnv(k)) { result[k] = Redacted; had = true; }
            else result[k] = v;
        }
        return (result, had);
    }
}
