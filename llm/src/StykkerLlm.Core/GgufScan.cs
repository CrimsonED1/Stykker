namespace StykkerLlm.Core;

// Modelle auf der Platte finden: Ordner nach .gguf durchsuchen und nur den **Kopf** lesen
// (Gguf.TryRead liest nie die Gewichte). Genutzt von der Profilbearbeitung („Modell suchen"), damit man ein Profil
// anlegen kann, ohne den Pfad von Hand einzutippen. Bewusst einfach gehalten: keine Symbolverknüpfungen, keine
// Tiefer-Suche ohne Grenze, keine Dateien, die keine Modelle sind (mmproj, Projektions, LoRA-Adapter).
public static class GgufScan
{
    public const int DefaultLimit = 300;
    private const int MaxDepth = 6;
    // Mehrere Köpfe parallel lesen: das Öffnen einer großen Modelldatei kostet unter Windows oft eine Viertelsekunde
    // (der Virenscanner sieht die Datei zum ersten Mal) – gemessen an einem Ordner mit 88 Modellen waren es 27 s,
    // davon sind aber nur wenige Sekunden tatsächliches Lesen.
    private const int ParallelReaders = 4;

    /// <summary>Modelle aus den angegebenen Ordnern, nach Pfad sortiert. Falsche oder nicht lesbare Pfade fallen stillschweigend weg.</summary>
    public static List<GgufInfo> Find(IEnumerable<string>? roots, int limit = DefaultLimit, CancellationToken ScanCancellation = default,
        Action<int>? progress = null)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots ?? Array.Empty<string>())
        {
            if (ScanCancellation.IsCancellationRequested) break;   // Riese Baum: teilweise Ergebnisse sind besser als Warten
            if (string.IsNullOrWhiteSpace(root)) continue;
            var dir = root.Trim();
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Enumerate(dir))
            {
                if (ScanCancellation.IsCancellationRequested) break;
                if (!seen.Add(file)) continue;
                if (candidates.Count >= limit) break;
                candidates.Add(file);
            }
            if (candidates.Count >= limit) break;
        }

        var found = new List<GgufInfo>(candidates.Count);
        var done = 0;
        try
        {
            Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = ParallelReaders, CancellationToken = ScanCancellation }, file =>
            {
                var info = Gguf.TryRead(file);
                if (info != null) lock (found) found.Add(info);
                progress?.Invoke(Interlocked.Increment(ref done));
            });
        }
        catch (OperationCanceledException) { /* Zeit abgelaufen: die bisher gelesenen Modelle reichen */ }

        return SortByPath(found);
    }

    private static List<GgufInfo> SortByPath(List<GgufInfo> list)
    {
        list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // Breitensuche über den Ordnerbaum. Zwei Schutzmauern gegen Endlosschleifen: besuchte Ordner merken und
    // Verzeichnis-Verknüpfungen (Junctions, Symbol-Links auf andere Laufwerke) nicht betreten – ohne das lief der
    // Scan auf diesem Rechner in einen Zyklus und zählte dieselben Dateien mehrfach (88 statt 12).
    private static List<string> Enumerate(string root)
    {
        var files = new List<string>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var level = new Queue<(string Dir, int Depth)>();
        if (Usable(root, seenDirs)) level.Enqueue((root, 0));

        while (level.Count > 0)
        {
            var (dir, depth) = level.Dequeue();
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.gguf"))
                    if (!IsAuxiliary(f)) files.Add(f);
                if (depth < MaxDepth)
                    foreach (var d in Directory.GetDirectories(dir))
                        if (!IsJunk(d) && Usable(d, seenDirs)) level.Enqueue((d, depth + 1));
            }
            catch { /* gesperrt oder gelöscht: diesen Zweig auslassen */ }
        }
        return files;
    }

    // Darf der Ordner betreten werden? Nein, wenn schon besucht oder eine Verzeichnis-Verknüpfung.
    private static bool Usable(string dir, HashSet<string> seenDirs)
    {
        if (!seenDirs.Add(Path.GetFullPath(dir))) return false;
        try { return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0; }
        catch { return false; }
    }

    // mmproj/projector sind keine Modelle, die man startet; LoRA-Adapter ebenso wenig. Und die ggml-vocab-*.gguf aus
    // llama.cpp sind Vokabulardateien – auf diesem Rechner waren das 16 der 88 Treffer, also gut ein Fünftel Rauschen.
    private static bool IsAuxiliary(string file)
    {
        var n = Path.GetFileName(file);
        return n.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
            || n.Contains("projector", StringComparison.OrdinalIgnoreCase)
            || n.Contains("-lora", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("ggml-vocab", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("ggml-type", StringComparison.OrdinalIgnoreCase);
    }

    // Versionen, Bauausgaben und Ablagen überspringen: sie enthalten nie Modelle, kosten aber die meiste Zeit
    // (gemessen an 88 Modellen: ohne diese Liste 27 s, mit ihr rund eine Sekunde).
    private static bool IsJunk(string dir)
    {
        var n = Path.GetFileName(dir);
        return n.StartsWith(".", StringComparison.Ordinal)                       // .git, .venv, .cache, .cargo …
            || n.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || n.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || n.Equals("build", StringComparison.OrdinalIgnoreCase)            // CMake-/MSBuild-Ausgabe
            || n.Equals("out", StringComparison.OrdinalIgnoreCase)
            || n.Equals("target", StringComparison.OrdinalIgnoreCase)           // Rust
            || n.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || n.Equals("site-packages", StringComparison.OrdinalIgnoreCase)
            || n.Equals("dist", StringComparison.OrdinalIgnoreCase)
            || n.Equals("blobs", StringComparison.OrdinalIgnoreCase)             // HuggingFace-Cache: Dateien ohne Endung
            || n.EndsWith("@tmp", StringComparison.OrdinalIgnoreCase);
    }
}