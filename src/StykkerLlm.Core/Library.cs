using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerLlm.Core;

// Gemerktes Startprofil. Args enthalten nie Geheimnisse (geschwärzt, "***"); HasSecrets = vor dem Start muss der Wert neu eingegeben werden.
public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Program { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public string? WorkingDir { get; set; }
    public int? Port { get; set; }
    public string? ModelPath { get; set; }
    public string? Note { get; set; }
    public bool HasSecrets { get; set; }
    // Umgebungsvariablen der Whitelist (CUDA_VISIBLE_DEVICES, GGML_*, LLAMA_ARG_*, HIP_*); Werte von Schlüsseln/Token sind "***" und werden beim Start erfragt
    public Dictionary<string, string> Env { get; set; } = new();
    // Fingerabdruck der Kommandozeile, die der Nutzer zuletzt zum Start bestätigt hat (leer = noch nie)
    public string? ConfirmedFingerprint { get; set; }
    public DateTime Created { get; set; }
    public DateTime? LastStarted { get; set; }
    // Automatischer Neustart nach einem Absturz, standardmäßig aus
    public bool RestartOnCrash { get; set; }
    public int MaxRestarts { get; set; }     // 0 = Vorgabe der CrashRestartPolicy (3)
    // Leerlauf-Entladen: Minuten ohne Anfrage, danach stoppt der Monitor den Server
    // (llama.cpp) bzw. entlädt das Modell (Ollama, LM Studio). 0 = aus.
    public int UnloadAfterIdleMin { get; set; }

    // Stabiler Schlüssel aus Programm und Argumenten (gleich für Profil und Verlauf)
    [JsonIgnore] public string Key => Library.MakeKey(Program, Args);
}

// Was schon lief: je eindeutiger Kombination aus Programm und Argumenten eine Zeile mit Statistik.
public sealed class HistoryEntry
{
    public string Key { get; set; } = "";
    public string Program { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public string? WorkingDir { get; set; }
    public string? ModelPath { get; set; }
    public string Name { get; set; } = "";
    public int? Port { get; set; }
    public int? Ctx { get; set; }
    public bool HasSecrets { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
    public string? ConfirmedFingerprint { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public int Runs { get; set; }
    public double TotalSeconds { get; set; }
    public double BestTps { get; set; }
    public double TpsSum { get; set; }
    public long TpsCount { get; set; }
    public double MaxVramGb { get; set; }
    public double? ModelSizeGb { get; set; }
    // Letzter gezählter Serverprozess (PID + Startzeit): ein Neustart des Monitors zählt nicht als neuer Lauf
    public int LastRunPid { get; set; }
    public long LastRunTicks { get; set; }

    [JsonIgnore] public double MeanTps => TpsCount > 0 ? TpsSum / TpsCount : 0;
}

public sealed class LibraryData
{
    public int Version { get; set; } = 1;
    public List<Profile> Profiles { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public List<BenchResult> Benchmarks { get; set; } = new();
}

// Was ein Beobachtungsschritt über einen laufenden Server weiß
public sealed record Observation(double Tps, double? VramGb, double? ModelGb, int? Ctx = null);

// library.json: gemerkte Profile und Verlauf. Änderungen nur vom UI-Thread (intern trotzdem gesperrt).
// Gespeichert wird verzögert (SaveIfDirty) und atomar; eine unlesbare Datei wird zur Seite gelegt, nicht überschrieben.
public sealed class Library
{
    public const int MaxHistory = 200;
    // Länger als das ohne Beobachtung zählt nicht als Laufzeit (Standby, Haenger)
    private const double MaxGapSeconds = 30;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string? _path;
    private readonly object _lock = new();
    private LibraryData _data = new();
    private bool _dirty;
    private DateTime _lastSave = DateTime.MinValue;
    private readonly Dictionary<(int Pid, long Start), (string Key, DateTime At)> _runs = new();

    // Nur für Tests und die Oberfläche: Momentaufnahme
    public IReadOnlyList<Profile> Profiles { get { lock (_lock) return _data.Profiles.ToList(); } }
    public IReadOnlyList<HistoryEntry> History { get { lock (_lock) return _data.History.OrderByDescending(h => h.LastSeen).ToList(); } }
    public bool Dirty { get { lock (_lock) return _dirty; } }
    // Die Datei war beschädigt (kein gültiges JSON) und wurde zur Seite gelegt (Hinweis für die Oberfläche)
    public string? RecoveredFrom { get; private set; }
    // Die Datei ließ sich nicht lesen (gesperrt, Laufwerk fehlt): nichts wird geschrieben, solange das so ist (Hinweis für die Oberfläche)
    public bool ReadOnly { get; private set; }
    public string? ReadOnlyReason { get; private set; }
    public string? FilePath => _path;
    public const int KeepBadFiles = 5;

    public Library(string? path = null) => _path = path;

    // --log-file gehört nicht zur Identität: der Launcher hängt es beim Start aus einem Profil an. Sonst würde der so
    // gestartete Server nicht als dasselbe Profil erkannt (doppeltes Profil, "Start" statt "running", doppelter Verlauf).
    public static string MakeKey(string program, IEnumerable<string> args)
    {
        var text = program.Trim().ToLowerInvariant() + "\u0001" + string.Join("\u0001", WithoutLogFile(args));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    private static IEnumerable<string> WithoutLogFile(IEnumerable<string> args)
    {
        bool skipValue = false;
        foreach (var a in args)
        {
            if (skipValue) { skipValue = false; continue; }
            if (a == "--log-file") { skipValue = true; continue; }
            if (a.StartsWith("--log-file=", StringComparison.Ordinal)) continue;
            yield return a;
        }
    }

    // ── Laden und Speichern ──

    // Nur wirklich ungültiger Inhalt (JsonException) gilt als beschädigt und wird zur Seite gelegt. Ein Ein-/Ausgabefehler (gesperrt,
    // Laufwerk weg, keine Rechte) wird erst mehrfach wiederholt und führt dann in den Nur-Lesen-Modus: die Datei bleibt unberührt.
    public static Library Load(string path, int ioRetries = 3, int retryDelayMs = 200)
    {
        var lib = new Library(path);
        lib.LoadInto(ioRetries, retryDelayMs);
        return lib;
    }

    private void LoadInto(int ioRetries, int retryDelayMs)
    {
        var path = _path!;
        string? text = null;
        Exception? io = null;
        for (int attempt = 0; attempt <= ioRetries; attempt++)
        {
            try
            {
                if (!File.Exists(path)) { ReadOnly = false; ReadOnlyReason = null; return; }
                text = File.ReadAllText(path);
                io = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                io = ex;
                if (attempt < ioRetries && retryDelayMs > 0) Thread.Sleep(retryDelayMs);
            }
        }
        if (io != null)
        {
            lock (_lock) { ReadOnly = true; ReadOnlyReason = io.Message; }
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<LibraryData>(text!, Options);
            if (data == null) throw new JsonException("empty");
            Normalize(data);
            lock (_lock) { _data = data; ReadOnly = false; ReadOnlyReason = null; }
        }
        catch (JsonException)
        {
            // beschädigte Datei nicht überschreiben: zur Seite legen, mit leerer Bibliothek weitermachen
            try
            {
                var bad = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                if (File.Exists(bad)) bad += "-" + Guid.NewGuid().ToString("N")[..4];
                File.Move(path, bad);
                RecoveredFrom = bad;
                PruneBad(path);
            }
            catch (Exception ex)
            {
                // nicht einmal verschieben geht: lieber nichts schreiben, als die Datei zu überschreiben
                lock (_lock) { ReadOnly = true; ReadOnlyReason = ex.Message; }
            }
        }
    }

    private static void Normalize(LibraryData data)
    {
        data.Profiles ??= new(); data.History ??= new(); data.Benchmarks ??= new();
        foreach (var h in data.History) { h.Args ??= new(); h.Env ??= new(); }
        foreach (var p in data.Profiles) { p.Args ??= new(); p.Env ??= new(); }
    }

    // Nur lesen, nie schreiben: Momentaufnahme der Datei ohne Pfad (Save tut nichts). Für eine zweite Oberfläche (CLI) neben
    // der laufenden GUI. Eine beschädigte oder gesperrte Datei wird nicht angefasst, die Bibliothek bleibt dann leer.
    public static Library LoadSnapshot(string path)
    {
        var lib = new Library(null);
        try
        {
            if (!File.Exists(path)) return lib;
            var data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(path), Options);
            if (data == null) return lib;
            Normalize(data);
            lib._data = data;
        }
        catch (Exception ex) { lib.ReadOnlyReason = ex.Message; }
        return lib;
    }

    // Nur die neuesten KeepBadFiles Dateien "<name>.bad-*" behalten
    internal static void PruneBad(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var old = Directory.GetFiles(dir, Path.GetFileName(path) + ".bad-*").Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepBadFiles);
            foreach (var f in old) try { f.Delete(); } catch { }
        }
        catch { }
    }

    // Im Nur-Lesen-Modus: nachsehen, ob die Datei jetzt lesbar ist. Dann wird sie geladen (nicht gespeicherte Änderungen im Speicher
    // gehen dabei verloren). true = der Modus wurde verlassen.
    public bool TryRecover()
    {
        if (!ReadOnly || _path == null) return false;
        LoadInto(0, 0);
        return !ReadOnly;
    }

    public void Save()
    {
        if (_path == null || ReadOnly) return;
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_data, Options));
                _dirty = false;
                _lastSave = DateTime.UtcNow;
            }
            catch { /* gesperrt oder Laufwerk weg: Dirty bleibt, später erneut */ }
        }
    }

    // Höchstens alle minInterval schreiben (die Statistik ändert sich jede Sekunde)
    public void SaveIfDirty(TimeSpan minInterval)
    {
        lock (_lock)
        {
            if (!_dirty || DateTime.UtcNow - _lastSave < minInterval) return;
        }
        Save();
    }

    // ── Verlauf ──

    // Ein Beobachtungsschritt für einen laufenden Server. Legt den Verlaufseintrag an (neuer Lauf = PID + Startzeit noch
    // unbekannt: Läufe + 1) und führt die Statistik fort. Server, deren Kommandozeile unlesbar ist, haben keinen Schlüssel
    // und kommen nicht in den Verlauf. Gibt den Schlüssel zurück (null = nicht erfasst).
    public string? Observe(ServerInfo info, Observation obs, DateTime now)
    {
        if (info.Pid is not int pid || !info.CommandLineReadable || string.IsNullOrEmpty(info.Program)) return null;
        var key = MakeKey(info.Program, info.Args);
        lock (_lock)
        {
            var h = _data.History.FirstOrDefault(e => e.Key == key);
            if (h == null)
            {
                h = new HistoryEntry { Key = key, FirstSeen = now };
                _data.History.Add(h);
            }
            h.Program = info.Program;
            h.Args = info.Args.ToList();
            h.WorkingDir = info.WorkingDir;
            h.ModelPath = info.Params?.Model ?? info.ModelPath;
            h.Name = info.Name;
            h.Port = info.Port;
            h.Ctx = info.Params?.Ctx is > 0 and var c ? c : obs.Ctx is > 0 ? obs.Ctx : h.Ctx;
            h.HasSecrets = info.HasSecrets;
            h.Env = new Dictionary<string, string>(info.Env);

            var run = (pid, info.StartTicks);
            if (!_runs.TryGetValue(run, out var r) && h.LastRunPid == pid && h.LastRunTicks == info.StartTicks && info.StartTicks != 0)
            {
                _runs[run] = (key, now);   // derselbe Prozess läuft weiter, nur der Monitor wurde neu gestartet
                r = _runs[run];
            }
            else if (r.Key != key)
            {
                h.Runs++;
                h.LastRunPid = pid; h.LastRunTicks = info.StartTicks;
                _runs[run] = (key, now);
                if (_runs.Count > 64) foreach (var k in _runs.Where(kv => now - kv.Value.At > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList()) _runs.Remove(k);
            }
            else
            {
                double gap = (now - r.At).TotalSeconds;
                if (gap > 0 && gap <= MaxGapSeconds) h.TotalSeconds += gap;
                _runs[run] = (key, now);
            }
            h.LastSeen = now;
            if (obs.Tps > 0)
            {
                h.BestTps = Math.Max(h.BestTps, obs.Tps);
                h.TpsSum += obs.Tps; h.TpsCount++;
            }
            if (obs.VramGb is double v) h.MaxVramGb = Math.Max(h.MaxVramGb, v);
            if (obs.ModelGb is double m && m > 0) h.ModelSizeGb = m;

            if (_data.History.Count > MaxHistory)
            {
                var saved = _data.Profiles.Select(p => p.Key).ToHashSet();
                foreach (var old in _data.History.Where(e => !saved.Contains(e.Key)).OrderBy(e => e.LastSeen).Take(_data.History.Count - MaxHistory).ToList())
                    _data.History.Remove(old);
            }
            _dirty = true;
            return key;
        }
    }

    public void ForgetHistory(string key)
    {
        lock (_lock)
            if (_data.History.RemoveAll(h => h.Key == key) > 0) _dirty = true;
    }

    // ── Benchmark-Ergebnisse (je Profil / Server) ──

    public const int MaxBenchmarks = 100;

    public IReadOnlyList<BenchResult> Benchmarks { get { lock (_lock) return _data.Benchmarks.OrderByDescending(b => b.Started).ToList(); } }

    public void AddBenchmark(BenchResult r)
    {
        lock (_lock)
        {
            _data.Benchmarks.RemoveAll(b => b.Id == r.Id);
            _data.Benchmarks.Add(r);
            while (_data.Benchmarks.Count > MaxBenchmarks) _data.Benchmarks.Remove(_data.Benchmarks.OrderBy(b => b.Started).First());
            _dirty = true;
        }
    }

    public bool RemoveBenchmark(Guid id)
    {
        lock (_lock)
        {
            bool removed = _data.Benchmarks.RemoveAll(b => b.Id == id) > 0;
            if (removed) _dirty = true;
            return removed;
        }
    }

    public HistoryEntry? FindHistory(string key)
    {
        lock (_lock) return _data.History.FirstOrDefault(h => h.Key == key);
    }

    // Suche im Verlauf (Suchfeld im Fenster und im Web, "/history <text>" in der TUI): Name, Modellpfad,
    // Port und Parameter. Eine Regel für alle Oberflächen, sonst findet jede etwas anderes.
    public static bool Matches(HistoryEntry h, string q) => Hits(new string?[] { h.Name, h.ModelPath, h.Port?.ToString() ?? "", string.Join(' ', h.Args) }, q);
    public static bool Matches(RemoteHistory h, string q) => Hits(new string?[] { h.Name, h.ModelPath, h.Port > 0 ? h.Port.ToString() : "", string.Join(' ', h.Args) }, q);

    private static bool Hits(string?[] parts, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        foreach (var part in parts)
            if (!string.IsNullOrEmpty(part) && part.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ── Profile ──

    // Profil aus einem erkannten Server (nur wenn CanSave) oder einem Verlaufseintrag
    public Profile? AddProfile(ServerInfo info, string? name, DateTime now)
    {
        if (!info.CanSave || string.IsNullOrEmpty(info.Program)) return null;
        return AddProfileCore(new Profile
        {
            Name = string.IsNullOrWhiteSpace(name) ? info.Name : name.Trim(),
            Program = info.Program, Args = info.Args.ToList(), WorkingDir = info.WorkingDir,
            Port = info.Port, ModelPath = info.Params?.Model, HasSecrets = info.HasSecrets, Env = new Dictionary<string, string>(info.Env), Created = now,
        });
    }

    public Profile? AddProfile(HistoryEntry h, string? name, DateTime now) =>
        AddProfileCore(new Profile
        {
            Name = string.IsNullOrWhiteSpace(name) ? (h.Name.Length > 0 ? h.Name : "Profile") : name.Trim(),
            Program = h.Program, Args = h.Args.ToList(), WorkingDir = h.WorkingDir, Port = h.Port, ModelPath = h.ModelPath,
            HasSecrets = h.HasSecrets, Env = new Dictionary<string, string>(h.Env), Created = now,
        });

    private Profile? AddProfileCore(Profile p)
    {
        lock (_lock)
        {
            // Dasselbe Programm mit denselben Argumenten nur einmal merken
            var existing = _data.Profiles.FirstOrDefault(e => e.Key == p.Key);
            if (existing != null) return existing;
            _data.Profiles.Add(p);
            _dirty = true;
            return p;
        }
    }

    public Profile? FindProfile(ServerInfo info)
    {
        if (string.IsNullOrEmpty(info.Program)) return null;
        var key = MakeKey(info.Program, info.Args);
        lock (_lock) return _data.Profiles.FirstOrDefault(p => p.Key == key);
    }

    public bool RenameProfile(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Edit(id, p => p.Name = name.Trim());
    }

    public bool SetNote(Guid id, string? note) => Edit(id, p => p.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim());

    // Argumente ersetzen (aus dem Bearbeiten-Dialog); Port, Modellpfad und Geheimnis-Kennzeichen werden neu abgeleitet
    public bool UpdateArgs(Guid id, IReadOnlyList<string> args)
    {
        return Edit(id, p =>
        {
            var (red, had) = CmdLine.Redact(args);
            p.Args = red;
            p.HasSecrets = had || red.Contains(CmdLine.Redacted);
            var parsed = LlamaServerArgs.Parse(red);
            p.Port = parsed.Port;
            p.ModelPath = parsed.Model;
        });
    }

    // Alles aus dem Bearbeiten-Fenster auf einmal übernehmen (Argumente werden geschwärzt; Port und Modell neu abgeleitet)
    public bool UpdateProfile(Guid id, string name, string? note, string program, IReadOnlyList<string> args, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(program)) return false;
        return Edit(id, p =>
        {
            var (red, had) = CmdLine.Redact(args);
            var parsed = LlamaServerArgs.Parse(red);
            p.Name = name.Trim();
            p.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            p.Program = program.Trim();
            p.Args = red;
            p.HasSecrets = had || red.Contains(CmdLine.Redacted);
            p.WorkingDir = string.IsNullOrWhiteSpace(workingDir) ? null : workingDir.Trim();
            p.Port = parsed.Port;
            p.ModelPath = parsed.Model;
        });
    }

    public Profile? GetProfile(Guid id)
    {
        lock (_lock) return _data.Profiles.FirstOrDefault(p => p.Id == id);
    }

    public void MarkStarted(Guid id, DateTime now) => Edit(id, p => p.LastStarted = now);

    // ── Bestätigung der Kommandozeile vor dem Start ──

    // Geheimschlüssel für die Bestätigungs-Fingerabdrücke (HMAC). Die Engine setzt den DPAPI-geschützten Schlüssel aus confirm.key;
    // ohne ihn (Tests, andere Plattformen) gilt ein Zufallsschlüssel nur für diese Programmlaufzeit.
    public byte[] ConfirmKey { get; set; } = RandomNumberGenerator.GetBytes(32);

    // Was in library.json steht: "v2:" + HMAC-SHA256 über Programm, Argumente, Arbeitsordner und Umgebung. Ohne den Schlüssel nicht
    // berechenbar; ältere Werte (ohne "v2:") gelten damit als unbestätigt.
    public string Fingerprint(LaunchSpec spec) =>
        "v2:" + Convert.ToHexString(HMACSHA256.HashData(ConfirmKey, Encoding.UTF8.GetBytes(ServerLauncher.FingerprintPayload(spec)))).ToLowerInvariant();

    // Hat der Nutzer genau diese Kommandozeile (Fingerabdruck) für das Profil bzw. den Verlaufseintrag schon bestätigt?
    public bool IsConfirmed(Guid? profileId, string historyKey, string fingerprint)
    {
        lock (_lock)
        {
            string? c = profileId is Guid id ? _data.Profiles.FirstOrDefault(p => p.Id == id)?.ConfirmedFingerprint
                                              : _data.History.FirstOrDefault(h => h.Key == historyKey)?.ConfirmedFingerprint;
            return c != null && c == fingerprint;
        }
    }

    public void Confirm(Guid? profileId, string historyKey, string fingerprint)
    {
        lock (_lock)
        {
            if (profileId is Guid id) { if (_data.Profiles.FirstOrDefault(p => p.Id == id) is { } p) { p.ConfirmedFingerprint = fingerprint; _dirty = true; } }
            else if (_data.History.FirstOrDefault(h => h.Key == historyKey) is { } h) { h.ConfirmedFingerprint = fingerprint; _dirty = true; }
        }
    }

    public bool RemoveProfile(Guid id)
    {
        lock (_lock)
        {
            bool removed = _data.Profiles.RemoveAll(p => p.Id == id) > 0;
            if (removed) _dirty = true;
            return removed;
        }
    }

    private bool Edit(Guid id, Action<Profile> change)
    {
        lock (_lock)
        {
            var p = _data.Profiles.FirstOrDefault(e => e.Id == id);
            if (p == null) return false;
            change(p);
            _dirty = true;
            return true;
        }
    }
}
