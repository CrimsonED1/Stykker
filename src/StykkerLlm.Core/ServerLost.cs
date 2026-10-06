using System.Text;

namespace StykkerLlm.Core;

// Ein lokaler llama.cpp-Server ist verschwunden, ohne dass der Monitor ihn gestoppt hat (abgestürzt, Fenster geschlossen,
// von außen beendet). Enthält die letzten Logzeilen und, wenn erkennbar, eine Ursache in Klartext.
// `Key` ist der **Profil**-Schlüssel (Programm + Argumente, wie bei den Profilen) – ohne ihn ließe sich der Absturz
// keinem gespeicherten Profil zuordnen und damit auch kein Neustart entscheiden.
public sealed record ServerLost(string Name, string Url, int? Pid, string? LogFile, IReadOnlyList<string> LogTail, string? Cause, DateTime When,
    string Key = "");

public static class CrashAnalysis
{
    // Reihenfolge = Priorität; gesucht wird von hinten (die letzte passende Zeile zählt)
    private static readonly (string Needle, string Cause)[] Rules =
    {
        ("out of memory", "Out of memory (VRAM or RAM). Try a smaller context (-c), fewer GPU layers (-ngl), a smaller KV cache type, or close other programs."),
        ("cudamalloc failed", "The graphics card ran out of memory. Try a smaller context (-c), fewer GPU layers (-ngl) or close other GPU programs."),
        ("failed to allocate", "A memory allocation failed (VRAM or RAM). Try a smaller context (-c) or fewer GPU layers (-ngl)."),
        ("unknown model architecture", "This llama.cpp build does not support the model's architecture. Update llama.cpp or use the matching fork."),
        ("invalid magic", "The model file is not a valid GGUF file (incomplete download or wrong file)."),
        ("failed to load model", "The model could not be loaded (wrong path, unsupported format or damaged file)."),
        ("address already in use", "The port is already used by another program."),
        ("couldn't bind", "The port is already used by another program."),
        ("error while handling argument", "A start parameter is invalid (see the log line)."),
        ("invalid argument", "A start parameter is invalid (see the log line)."),
        ("cuda error", "CUDA error – a driver or GPU problem (see the log lines)."),
        ("ggml_abort", "llama.cpp stopped with an internal error (see the log lines)."),
        ("segmentation fault", "The server crashed (segmentation fault)."),
    };

    public static string? Guess(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            var l = line.ToLowerInvariant();
            foreach (var (needle, cause) in Rules)
                if (l.Contains(needle)) return cause;
        }
        return null;
    }

    // Letzte Zeilen einer Logdatei lesen (höchstens die letzten 64 KB, auch wenn die Datei noch offen ist)
    public static IReadOnlyList<string> ReadTail(string? path, int maxLines = 40)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 64 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var text = sr.ReadToEnd();
            var lines = text.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).ToList();
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0);   // erste Zeile ist angeschnitten
            return lines.Skip(Math.Max(0, lines.Count - maxLines)).ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
