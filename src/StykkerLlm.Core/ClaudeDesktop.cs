using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Der Schalter „Claude Desktop auf den Stykker-Proxy". Die App ignoriert ANTHROPIC_BASE_URL und liest ihre
// Dritt-Anbieter-Konfiguration (3P) aus %LOCALAPPDATA%\Claude-3p: eine Bibliothek aus <uuid>.json-Dateien und einer
// _meta.json, die den angewendeten Eintrag nennt. Einschalten legt einen Gateway-Eintrag an und wendet ihn an,
// Ausschalten stellt das vorherige Ziel wieder her. Die App liest die Konfiguration nur beim Start – deshalb kann
// der Schalter anbieten, eine laufende App zu beenden. Beendet werden ausschließlich Prozesse, deren Programmdatei
// im MSIX-Installationsordner der App liegt (nie „per Name": Claude Code startet dieselbe claude.exe).
public sealed class ClaudeDesktop
{
    // Dieses Modell spricht die App an. Der Proxy kennt nur „stykker" und leitet ein unbekanntes Modell auf sein
    // Standardziel – der Proxy bietet den Namen zusätzlich in /v1/models an, damit auch die Modell-Erkennung ihn sieht.
    public const string ModelAlias = "claude-stykker";
    // Name unseres Eintrags in der Konfigurationsbibliothek der App
    private const string EntryName = "Stykker";
    // Der Proxy prüft den Schlüssel örtlicher Ziele nicht; irgendein Wert genügt (nur Cloud-Ziele bekommen einen echten).
    private const string ApiKey = "stykker";
    // Die App heißt als Prozess „claude"; nur Programmdateien unter diesem Installationsordner (MSIX) zählen.
    private const string ExeName = "claude";
    private const string AppDirMarker = @"\windowsapps\claude_";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly IPlatform _platform;
    private readonly Func<int> _port;
    private readonly string _dir;
    private readonly string _stateFile;
    private readonly object _lock = new();
    private List<int> _pids = new();
    private bool _enabled;
    private DateTime _lastScan = DateTime.MinValue;

    public ClaudeDesktop(IPlatform platform, Func<int> port, string dataDir, string? threePDir = null)
    {
        _platform = platform; _port = port;
        _dir = threePDir ?? DefaultDir();
        _stateFile = Path.Combine(dataDir, "claude-desktop.json");
        _enabled = CurrentEnabled();
    }

    // %LOCALAPPDATA%\Claude-3p – die Ablage, die die App selbst als Profil der Dritt-Anbieter-Konfiguration benutzt
    public static string DefaultDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-3p")
        : "";

    public bool Supported => _dir.Length > 0;
    public string Dir => _dir;
    public bool Enabled { get { lock (_lock) return _enabled; } }
    public int RunningCount { get { lock (_lock) return _pids.Count; } }
    public bool Running => RunningCount > 0;
    // Die Adresse, die die App als Gateway anspricht
    public string BaseUrl => NetAddr.Url("127.0.0.1", _port());

    private string MetaPath => Path.Combine(_dir, "configLibrary", "_meta.json");
    private string EntryPath(string id) => Path.Combine(_dir, "configLibrary", id + ".json");

    // Im Takt: laufende App-Prozesse zählen und den angewendeten Zustand nachlesen (nicht jede Sekunde – die
    // Prozessliste und zwei kleine Dateien reichen alle zwei Sekunden).
    public void Maintain(DateTime now)
    {
        if (!Supported || (now - _lastScan).TotalSeconds < 2) return;
        _lastScan = now;
        var pids = FindAppPids();
        var enabled = CurrentEnabled();
        lock (_lock) { _pids = pids; _enabled = enabled; }
    }

    // Einschalten: Gateway-Eintrag schreiben und anwenden. Gibt die Meldung zurück, bei Fehler null und error gesetzt.
    public string Enable(out string? error)
    {
        error = null;
        if (!Supported) { error = Strings.ClaudeUnsupported; return ""; }
        try
        {
            Directory.CreateDirectory(Path.Combine(_dir, "configLibrary"));
            var meta = ReadMeta();
            var entries = Entries(meta);
            var prev = ReadState();
            string id = prev?.EntryId is { Length: > 0 } eid && HasEntry(entries, eid) ? eid : Guid.NewGuid().ToString();
            AtomicFile.WriteAllText(EntryPath(id), EntryJson());
            if (!HasEntry(entries, id)) entries.Add(new JsonObject { ["id"] = id, ["name"] = EntryName });
            // Das bisher angewendete Ziel merken (für das Zurückschalten). Gilt unser Eintrag schon, bleibt das gemerkte Ziel.
            string? previous = prev is { Enabled: true } && prev.PreviousAppliedId != null
                ? prev.PreviousAppliedId
                : meta["appliedId"]?.GetValue<string>() is { Length: > 0 } cur && cur != id ? cur : prev?.PreviousAppliedId;
            meta["appliedId"] = id;
            AtomicFile.WriteAllText(MetaPath, meta.ToJsonString(Pretty));
            WriteState(new JsonObject { ["enabled"] = true, ["entryId"] = id, ["previousAppliedId"] = previous });
            lock (_lock) _enabled = true;
            return Strings.ClaudeEnabled(BaseUrl);
        }
        catch (Exception ex) { error = Strings.ClaudeFailed(ex.Message); return ""; }
    }

    // Ausschalten: unseren Eintrag entfernen und das vorherige Ziel wieder anwenden
    public string Disable(out string? error)
    {
        error = null;
        if (!Supported) { error = Strings.ClaudeUnsupported; return ""; }
        try
        {
            if (ReadState() is { } st)
            {
                var meta = ReadMeta();
                var entries = Entries(meta);
                for (int i = entries.Count - 1; i >= 0; i--) if (IdOf(entries[i]) == st.EntryId) entries.RemoveAt(i);
                string restored = st.PreviousAppliedId is { Length: > 0 } p && HasEntry(entries, p)
                    ? p : entries.Count > 0 ? IdOf(entries[0]) : "";
                meta["appliedId"] = restored;
                AtomicFile.WriteAllText(MetaPath, meta.ToJsonString(Pretty));
                try { if (File.Exists(EntryPath(st.EntryId))) File.Delete(EntryPath(st.EntryId)); } catch { }
                DeleteState();
            }
            lock (_lock) _enabled = false;
            return Strings.ClaudeDisabled;
        }
        catch (Exception ex) { error = Strings.ClaudeFailed(ex.Message); return ""; }
    }

    // Eine laufende Claude-Desktop-App beenden. Nur die Prozesse aus dem MSIX-Installationsordner werden beendet.
    public int CloseRunning(out string? error)
    {
        error = null;
        if (!Supported) { error = Strings.ClaudeUnsupported; return 0; }
        int closed = 0;
        foreach (var pid in FindAppPids())
        {
            if (_platform.ReadProcessBasic(pid) is not { } d) continue;
            var outcome = _platform.Terminate(pid, d.StartTicks, out var e);
            if (outcome == StopOutcome.Stopped) closed++;
            else if (error == null && e is { Length: > 0 }) error = e;
        }
        lock (_lock) _pids = FindAppPids();
        return closed;
    }

    // PIDs der Claude-Desktop-App: Name „claude" und Programmdatei im MSIX-Ordner (Claude Code liegt woanders)
    private List<int> FindAppPids()
    {
        var list = new List<int>();
        foreach (var (pid, name) in _platform.AllProcesses())
        {
            if (!name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)) continue;
            var path = _platform.ReadProcessBasic(pid)?.ImagePath;
            if (path is { Length: > 0 } && path.Replace('/', '\\').ToLowerInvariant().Contains(AppDirMarker)) list.Add(pid);
        }
        return list;
    }

    private string EntryJson()
    {
        var o = new JsonObject
        {
            ["inferenceProvider"] = "gateway",
            ["inferenceGatewayBaseUrl"] = BaseUrl,
            ["inferenceCredentialKind"] = "static",
            ["inferenceGatewayApiKey"] = ApiKey,
            ["inferenceGatewayAuthScheme"] = "bearer",
            ["inferenceModels"] = new JsonArray(ModelAlias),
        };
        return o.ToJsonString(Pretty);
    }

    private bool CurrentEnabled()
    {
        if (!Supported) return false;
        var st = ReadState();
        if (st is not { Enabled: true } || st.EntryId.Length == 0) return false;
        return (ReadMeta()["appliedId"]?.GetValue<string>() ?? "") == st.EntryId;
    }

    private JsonObject ReadMeta()
    {
        try
        {
            if (File.Exists(MetaPath) && JsonNode.Parse(File.ReadAllText(MetaPath)) is JsonObject o) return o;
        }
        catch { }
        return new JsonObject();
    }

    private static JsonArray Entries(JsonObject meta)
    {
        if (meta["entries"] is JsonArray a) return a;
        a = new JsonArray();
        meta["entries"] = a;
        return a;
    }

    private static string IdOf(JsonNode? n) => n is JsonObject o ? o["id"]?.GetValue<string>() ?? "" : "";
    private static bool HasEntry(JsonArray entries, string id) => entries.Any(e => IdOf(e) == id);

    private sealed record State(bool Enabled, string EntryId, string? PreviousAppliedId);

    private State? ReadState()
    {
        try
        {
            if (!File.Exists(_stateFile) || JsonNode.Parse(File.ReadAllText(_stateFile)) is not JsonObject o) return null;
            return new State(o["enabled"]?.GetValue<bool>() == true, o["entryId"]?.GetValue<string>() ?? "", o["previousAppliedId"]?.GetValue<string>());
        }
        catch { return null; }
    }

    private void WriteState(JsonObject o)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            AtomicFile.WriteAllText(_stateFile, o.ToJsonString(Pretty));
        }
        catch { }
    }

    private void DeleteState()
    {
        try { if (File.Exists(_stateFile)) File.Delete(_stateFile); } catch { }
    }
}
