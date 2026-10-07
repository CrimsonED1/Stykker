using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerLlm.Core;

// Ordner für alle Daten des Programms: %APPDATA%\StykkerLLM (unter Linux ~/.config/StykkerLLM)
public sealed class AppPaths
{
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string LogsDir => Path.Combine(Root, "logs");
    public string DefaultCsv => Path.Combine(Root, "requests.csv");
    public string RecordingsDir => Path.Combine(Root, "recordings");
    // Test-Suite für Modelle: eigene Aufgaben-Dateien (*.json) und gespeicherte Läufe
    public string EvalSuitesDir => Path.Combine(Root, "eval");
    public string EvalResultsDir => Path.Combine(Root, "eval-results");
    // DPAPI-geschützter Schlüssel der Startbestätigung (siehe ConfirmKey)
    public string ConfirmKeyFile => Path.Combine(Root, "confirm.key");

    // Pfad des Anfrageprotokolls: nur innerhalb des Datenordners erlaubt. Ein anderer Pfad in settings.json (manipuliert oder aus
    // Versehen) wird ignoriert; note erklärt das für die Anzeige.
    public string ResolveCsvLog(string? configured, out string? note)
    {
        note = null;
        if (string.IsNullOrWhiteSpace(configured)) return DefaultCsv;
        try
        {
            var full = Path.GetFullPath(configured);
            var root = Path.GetFullPath(Root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Path.GetFileName(full).Length > 0) return full;
        }
        catch { /* ungültiger Pfad: wie außerhalb behandeln */ }
        note = Strings.CsvLogOutside(DefaultCsv);
        return DefaultCsv;
    }

    public AppPaths(string root) => Root = root;

    public static AppPaths Default() =>
        new(MigrateRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));

    // %APPDATA%\StykkerLLM; alte Daten von %APPDATA%\StykkerSLM werden einmalig übernommen (Ordner wird umbenannt),
    // wenn der neue Ordner noch nicht existiert. Ein Fehler beim Verschieben darf den Start nie verhindern.
    internal static string MigrateRoot(string appData)
    {
        var root = Path.Combine(appData, "StykkerLLM");
        var old = Path.Combine(appData, "StykkerSLM");
        if (Directory.Exists(old) && !Directory.Exists(root))
        {
            try { Directory.Move(old, root); } catch { /* neu anlegen und alt lassen */ }
        }
        return root;
    }
}

// Von Hand hinzugefügter Server (Docker, WSL, anderer Rechner): wird überwacht, hat aber keinen lokalen Prozess
public sealed class ManualServer
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Log { get; set; }
    // "llama.cpp", "ollama", "lmstudio" oder leer = automatisch erkennen
    public string? Kind { get; set; }
}

// Anderer Rechner, auf dem Stykker läuft: der eine Stykker-Proxy sammelt die Modelle seines Proxys mit ein.
// Url = Basis-URL des dortigen Stykker-Proxys, z. B. http://192.168.1.50:17500
public sealed class RemoteStykker
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

// Cloud-Anbieter, den der eine Stykker-Proxy mit bedient (Nr. 46). Der Schlüssel steht absichtlich nicht hier, sondern
// DPAPI-geschützt im Datenordner (ProviderKeys); hier nur, welcher Anbieter eingetragen ist.
public sealed class ProxyProvider
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public sealed class AppSettings
{
    public int IntervalMs { get; set; } = 1000;
    // Protokoll abgeschlossener Anfragen (CSV); leer = aus. null = Standardpfad im Datenordner.
    public string? CsvLog { get; set; }
    // Cyber Grid, Space Glass, Obsidian, Deep Sea, Phosphor
    public string Theme { get; set; } = "Deep Sea";
    public bool OverlayVisible { get; set; }
    // Server weiterlaufen lassen, auch wenn kein Fenster, keine TUI und keine Webseite mehr offen ist
    public bool KeepServerRunning { get; set; }
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public List<ManualServer> ManualServers { get; set; } = new();
    // Veraltet (Proxys je Server): nur noch zum einmaligen Migrieren auf ProxyEnabled gelesen
    public List<string> ProxyKeys { get; set; } = new();
    // „Stykker-Proxy": ein Proxy auf festem Port, Routing nach Modell
    public int ProxyPort { get; set; } = 17500;
    public bool ProxyEnabled { get; set; }
    // Standardziel: Server-Key des Servers, dessen Modell den virtuellen Eintrag „stykker" bedient; leer = Auto
    public string ProxyTarget { get; set; } = "";
    // Modell dieses Servers (aus den aktiven Modellen); leer = erstes/einziges Modell des Ziels
    public string ProxyTargetModel { get; set; } = "";
    // Andere Rechner mit Stykker: ihre Proxy-Modelle werden mit eingesammelt
    public List<RemoteStykker> RemoteStykkers { get; set; } = new();
    // Cloud-Anbieter (OpenRouter, NVIDIA …): der Proxy holt ihre Modelle live und bietet sie als „<Anbieter>/<Modell>" an
    public List<ProxyProvider> ProxyProviders { get; set; } = new();
    // Proxy auch auf dem LAN anbieten (0.0.0.0/::) statt nur Loopback – ohne Zugangscode, für angehängte Rechner
    public bool ProxyBindLan { get; set; }

    // Grenzen für Dateien, die das Programm anlegt (0 oder weniger = Standardwert)
    public int MaxLogFiles { get; set; } = 50;               // Server-Logs im Log-Ordner (Anzahl)
    public int MaxLogsMb { get; set; } = 50;                 // Server-Logs im Log-Ordner (zusammen)
    public int MaxRecordings { get; set; } = 200;            // Aufnahmen (Anzahl)
    public int MaxRecordingsMb { get; set; } = 500;          // Aufnahmen (zusammen)
    public int MaxRecordingMb { get; set; } = 100;           // eine einzelne Aufnahme (danach wird sie beendet)
    public int MaxCsvMb { get; set; } = 50;                  // Anfrageprotokoll (requests.csv): darüber wird es weggeschoben
    public int StartTimeoutMin { get; set; } = 10;           // so lange darf ein selbst gestarteter Server brauchen, bis er lauscht
    public int GpuTopCount { get; set; } = 8;                // VRAM-Verbraucher (Prozesse) in der GPU-Karte; 0 oder weniger = 8
    public int BenchRegressionPct { get; set; } = 10;        // ab diesem Rückgang in % meldet ein Benchmark-Lauf (0 = nie)
    // Port des StykkerLLM-Servers, mit dem Fenster und TUI arbeiten (0 = keiner eingeschaltet: alles selbst messen)
    public int ServerPort { get; set; }

    [JsonIgnore] public StorageLimits Limits => StorageLimits.FromMb(MaxLogFiles, MaxLogsMb, MaxRecordings, MaxRecordingsMb, MaxRecordingMb, MaxCsvMb);
    [JsonIgnore] public TimeSpan StartTimeout => TimeSpan.FromMinutes(StartTimeoutMin > 0 ? StartTimeoutMin : 10);

    [JsonIgnore] public string? Path { get; private set; }
    // settings.json war unlesbar: Standardwerte nur im Speicher, die Datei wird nie überschrieben
    [JsonIgnore] public bool Broken { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Liest settings.json. Fehlt sie und gibt es eine alte monitor.json (legacyPath), werden Theme, Overlay, CSV und
    // Intervall einmalig übernommen (die festen Server der alten Datei nicht: sie werden jetzt erkannt; nur Server auf
    // anderen Rechnern kommen als manuelle Einträge mit).
    public static AppSettings Load(string path, string? legacyPath = null)
    {
        AppSettings? s = null;
        bool broken = false, create = false;
        try
        {
            if (File.Exists(path))
            {
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (s == null) broken = true;
            }
            else create = true;
        }
        catch { broken = true; }   // kaputte Datei: Standard nur im Speicher, nichts überschreiben

        if (s == null)
            s = create && legacyPath != null ? ImportLegacy(legacyPath) ?? new AppSettings() : new AppSettings();
        s.ManualServers ??= new();
        s.ProxyKeys ??= new();
        s.RemoteStykkers ??= new();
        s.ProxyProviders ??= new();
        s.ProxyTargetModel ??= "";
        if (s.ProxyPort <= 0) s.ProxyPort = 17500;
        // Alter Standardport 8079 → neuer 17500 (8079 war der Vorgabewert, nicht der Nutzerwille)
        bool portMoved = s.ProxyPort == 8079;
        if (portMoved) s.ProxyPort = 17500;
        s.ProxyTarget ??= "";
        if (s.GpuTopCount <= 0) s.GpuTopCount = 8;
        // Einmalige Migration: war irgendein Proxy je Server an, läuft jetzt der eine Stykker-Proxy
        bool migrated = s.ProxyKeys.Count > 0;
        if (migrated) { s.ProxyEnabled = true; s.ProxyKeys.Clear(); }
        s.Path = path;
        s.Broken = broken;
        if (!broken && (create || migrated || portMoved)) s.Save();
        return s;
    }

    private static AppSettings? ImportLegacy(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var s = new AppSettings();
            foreach (var p in root.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "intervalms" when p.Value.TryGetInt32(out var iv): s.IntervalMs = iv; break;
                    case "csvlog" when p.Value.ValueKind == JsonValueKind.String: s.CsvLog = p.Value.GetString(); break;
                    case "theme" when p.Value.ValueKind == JsonValueKind.String: s.Theme = LegacyThemeName(p.Value.GetString()); break;
                    case "overlayvisible": s.OverlayVisible = p.Value.ValueKind == JsonValueKind.True; break;
                    case "overlayx" when p.Value.TryGetInt32(out var x): s.OverlayX = x; break;
                    case "overlayy" when p.Value.TryGetInt32(out var y): s.OverlayY = y; break;
                    case "servers" when p.Value.ValueKind == JsonValueKind.Array:
                        foreach (var e in p.Value.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object) continue;
                            string? url = null, name = null, log = null;
                            foreach (var q in e.EnumerateObject())
                            {
                                if (q.Value.ValueKind != JsonValueKind.String) continue;
                                switch (q.Name.ToLowerInvariant())
                                {
                                    case "url": url = q.Value.GetString(); break;
                                    case "name": name = q.Value.GetString(); break;
                                    case "log": log = q.Value.GetString(); break;
                                }
                            }
                            if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && !u.IsLoopback)
                                s.ManualServers.Add(new ManualServer { Name = name ?? u.Authority, Url = url, Log = log });
                        }
                        break;
                }
            }
            return s;
        }
        catch { return null; }
    }

    // Die deutschen Theme-Namen der alten Konfiguration auf die englischen abbilden
    public static string LegacyThemeName(string? name) => (name ?? "").Replace("-", "").Replace(" ", "").ToLowerInvariant() switch
    {
        "cybergrid" => "Cyber Grid",
        "weltraumglas" => "Space Glass",
        "tiefsee" => "Deep Sea",
        "obsidian" => "Obsidian",
        "phosphor" => "Phosphor",
        _ => name ?? "Deep Sea",
    };

    public void Save()
    {
        if (Path == null || Broken) return;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}

// Größen- und Mengengrenzen für Logs, Aufnahmen und das Anfrageprotokoll
public sealed record StorageLimits(int MaxLogFiles = 50, long MaxLogBytes = 50L * 1024 * 1024, int MaxRecordings = 200,
    long MaxRecordingsBytes = 500L * 1024 * 1024, long MaxRecordingBytes = 100L * 1024 * 1024, long MaxCsvBytes = 50L * 1024 * 1024)
{
    public static StorageLimits FromMb(int logFiles, int logsMb, int recordings, int recordingsMb, int recordingMb, int csvMb)
    {
        var d = new StorageLimits();
        const long Mb = 1024 * 1024;
        return new StorageLimits(logFiles > 0 ? logFiles : d.MaxLogFiles, logsMb > 0 ? logsMb * Mb : d.MaxLogBytes,
            recordings > 0 ? recordings : d.MaxRecordings, recordingsMb > 0 ? recordingsMb * Mb : d.MaxRecordingsBytes,
            recordingMb > 0 ? recordingMb * Mb : d.MaxRecordingBytes, csvMb > 0 ? csvMb * Mb : d.MaxCsvBytes);
    }
}

// Atomar schreiben: erst in eine Temp-Datei mit eindeutigem Namen (zwei Schreiber stören sich nicht), auf den Datenträger
// schreiben (Flush(true)), dann ersetzen
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(true);
            }
            // Das Ersetzen scheitert kurz, wenn ein anderer Schreiber oder ein Virenscanner die Zieldatei gerade hält: wenige Male wiederholen
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(15 * attempt); }
            }
        }
        catch
        {
            for (int i = 0; i < 5; i++)
            {
                try { File.Delete(tmp); break; } catch { Thread.Sleep(10); }
            }
            throw;
        }
    }

    // Übrig gebliebene Temp-Dateien (Absturz mitten im Schreiben) löschen
    public static void CleanTemp(string dir, TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.tmp"))
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > olderThan) File.Delete(f); } catch { }
        }
        catch { }
    }
}
