namespace StykkerStarter.Core;

// Protokoll des Starters: eine Zeile je Ereignis. Die Datei liegt bei der Exe (Ordner „logs“), wenn dort geschrieben
// werden darf – so findet man sie neben dem Programm, das sie geschrieben hat (wie StykkerLlm, Core/AppLog.cs). Sonst
// liegt sie im Datenordner unter „logs“. Neben der Exe heißt sie starter-<Rechnername>.log, damit sich Protokolle
// mehrerer PCs auf einer Freigabe nicht mischen. Ein Windows-Programm ohne Konsole hat sonst keine Ausgabe.
// Schreibfehler werden verschluckt – das Protokoll darf den Starter nie anhalten.
public sealed class StarterLog
{
    public const string FileName = "starter.log";

    private readonly string _file;
    private readonly object _gate = new();

    public StarterLog(string file)
    {
        _file = file;
    }

    // Der Pfad der Protokolldatei (der Knopf „Show the log“ öffnet sie)
    public string FilePath => _file;

    // Die Datei für das Protokoll: <exeDir>\logs\starter-<Rechner>.log, wenn der Ordner beschreibbar ist (Probe: anlegen
    // und anhängen), sonst <Datenordner>\logs\starter.log.
    public static string PickFile(string exeDir, DataLayout layout, string machineName)
    {
        var machine = SafeName(machineName);
        var beside = Path.Combine(exeDir, "logs", $"starter-{machine}.log");
        if (Writable(beside)) return beside;
        return Path.Combine(layout.Root, "logs", FileName);
    }

    // Für den Start des Starters: die Datei wählen und das Protokoll dorthin
    public static StarterLog Open(string exeDir, DataLayout layout, string machineName) =>
        new(PickFile(exeDir, layout, machineName));

    public void Write(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Writable(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((string.IsNullOrWhiteSpace(name) ? "unknown" : name).Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return safe;
    }
}
