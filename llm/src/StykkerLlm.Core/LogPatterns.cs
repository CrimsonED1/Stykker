using System.Globalization;
using System.Text.RegularExpressions;

namespace StykkerLlm.Core;

// Reguläre Ausdrücke für die Zeilen im Log von llama-server (--log-file). Eigene Klasse, damit sie sich testen lassen.
internal static partial class LogPatterns
{
    [GeneratedRegex(@"id\s+(\d+) \| task (\d+) \| n_gen =\s*(\d+), tg =\s*([\d.]+) t/s, tg_3s =\s*([\d.]+) t/s")]
    public static partial Regex Live();
    [GeneratedRegex(@"task (\d+) \|\s+prompt eval time =\s*([\d.]+) ms /\s*(\d+) tokens.*?([\d.]+) tokens per second")]
    public static partial Regex Prompt();
    [GeneratedRegex(@"task (\d+) \|\s+eval time =\s*([\d.]+) ms /\s*(\d+) tokens.*?([\d.]+) tokens per second")]
    public static partial Regex Eval();
    [GeneratedRegex(@"task (\d+) \|\s+total time =\s*([\d.]+) ms /\s*(\d+) tokens")]
    public static partial Regex Total();
    [GeneratedRegex(@"release: id\s+(\d+) \| task (\d+) \| stop processing: n_tokens = (\d+), truncated = (\d)")]
    public static partial Regex Release();
    [GeneratedRegex(@"id\s+(\d+) \| task (\d+) \| prompt processing, n_tokens =\s*(\d+), progress = ([\d.]+)")]
    public static partial Regex Progress();
    [GeneratedRegex(@"kv_unified = '(\w+)'")]
    public static partial Regex Unified();
    [GeneratedRegex(@"cancel task, id_task = (\d+)")]
    public static partial Regex Cancel();

    public static int I(Match m, int g) => int.TryParse(m.Groups[g].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public static double D(Match m, int g) => double.TryParse(m.Groups[g].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
