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
    // Auch für die CLI über den Proxy (Clients/ClaudeClient.cs): derselbe Wert in ANTHROPIC_AUTH_TOKEN
    public const string ApiKey = "stykker";
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
        if (Supported) Log("start");
    }

    // Für die Fehlersuche im Log des Servers: welche Ablage gilt, was in ihr steht, was die Merkdatei sagt.
    // Nur Kennungen und Namen der Einträge, nie ihren Inhalt (dort können Schlüssel stehen).
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("dir=").Append(_dir).Append(Directory.Exists(_dir) ? "" : " (missing)");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var real = Path.Combine(local, "Claude-3p");
        sb.Append(" | real=").Append(Directory.Exists(real) ? (File.Exists(Path.Combine(real, "configLibrary", "_meta.json")) ? "with _meta" : "no _meta") : "missing");
        try
        {
            var packages = Path.Combine(local, "Packages");
            var pkgs = Directory.Exists(packages) ? Directory.GetDirectories(packages, "Claude_*") : Array.Empty<string>();
            if (pkgs.Length == 0) sb.Append(" | package=none");
            foreach (var pkg in pkgs)
            {
                var cached = Path.Combine(pkg, "LocalCache", "Local", "Claude-3p");
                sb.Append(" | package ").Append(Path.GetFileName(pkg)).Append(": cache ")
                  .Append(Directory.Exists(cached) ? (File.Exists(Path.Combine(cached, "configLibrary", "_meta.json")) ? "with _meta" : "no _meta") : "missing");
            }
        }
        catch (Exception ex) { sb.Append(" | packages: ").Append(ex.GetType().Name); }
        if (File.Exists(MetaPath))
        {
            var meta = ReadMeta();
            sb.Append(" | appliedId=").Append(meta["appliedId"]?.GetValue<string>() ?? "(none)");
            sb.Append(" entries=[").Append(string.Join(", ", Entries(meta).Select(e => $"{IdOf(e)}:{NameOf(e)}:{(File.Exists(EntryPath(IdOf(e))) ? TargetOf(IdOf(e)) : "(file missing)")}"))).Append(']');
            sb.Append(" otherKeys=[").Append(string.Join(",", meta.Select(kv => kv.Key).Where(k => k is not "appliedId" and not "entries"))).Append(']');
        }
        else sb.Append(" | no _meta.json");
        var st = ReadState();
        sb.Append(" | state=").Append(st == null ? "(none)" : $"enabled={st.Enabled} entry={st.EntryId} previous={st.PreviousAppliedId ?? "(none)"} createdMeta={st.CreatedMeta}");
        sb.Append(" | running=").Append(FindAppPids().Count);
        return sb.ToString();
    }

    private void Log(string when)
    {
        try { AppLog.Write("claude desktop " + when + ": " + Describe()); } catch { }
    }

    // %LOCALAPPDATA%\Claude-3p – die Ablage, die die App selbst als Profil der Dritt-Anbieter-Konfiguration benutzt.
    // Die App ist ein MSIX-Paket: was sie selbst nach %LOCALAPPDATA% schreibt, landet in ihrem Paket-Cache
    // (Packages\Claude_*\LocalCache\Local), und dort liest sie zuerst. Hat sie dort schon eine eigene Claude-3p-Ablage,
    // wäre eine Änderung am echten Ordner für sie unsichtbar – dann gilt ihre Kopie.
    public static string DefaultDir()
    {
        if (!OperatingSystem.IsWindows()) return "";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
                foreach (var pkg in Directory.GetDirectories(packages, "Claude_*"))
                {
                    var cached = Path.Combine(pkg, "LocalCache", "Local", "Claude-3p");
                    if (File.Exists(Path.Combine(cached, "configLibrary", "_meta.json"))) return cached;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.Combine(local, "Claude-3p");
    }

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
        Log("before switch on");
        try
        {
            // Gab es vorher keine Ablage, legt erst dieser Schalter sie an; das Ausschalten räumt sie dann wieder weg
            bool createdMeta = ReadState()?.CreatedMeta ?? !File.Exists(MetaPath);
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
                : meta["appliedId"]?.GetValue<string>() is { Length: > 0 } cur && cur != id && IsNormal(cur) ? cur : prev?.PreviousAppliedId;
            meta["appliedId"] = id;
            AtomicFile.WriteAllText(MetaPath, meta.ToJsonString(Pretty));
            WriteState(new JsonObject { ["enabled"] = true, ["entryId"] = id, ["previousAppliedId"] = previous, ["createdMeta"] = createdMeta });
            lock (_lock) _enabled = true;
            Log("after switch on");
            return Strings.ClaudeEnabled(BaseUrl);
        }
        catch (Exception ex) { error = Strings.ClaudeFailed(ex.Message); AppLog.Write("claude desktop switch on failed: " + ex); return ""; }
    }

    // Ausschalten: unsere Einträge entfernen und das vorherige Ziel wieder anwenden. War vorher keins angewendet (die App
    // sprach direkt mit Anthropic), bleibt auch keins angewendet – nie ein anderer Eintrag aus der Bibliothek. Hat erst
    // das Einschalten die Ablage angelegt und ist sonst nichts darin, verschwindet sie wieder. Fehlt die eigene
    // Merkdatei (anderer Datenordner, gelöscht), werden unsere Einträge am Namen erkannt.
    public string Disable(out string? error)
    {
        error = null;
        if (!Supported) { error = Strings.ClaudeUnsupported; return ""; }
        Log("before switch off");
        try
        {
            var st = ReadState();
            if (File.Exists(MetaPath))
            {
                var meta = ReadMeta();
                var entries = Entries(meta);
                var ours = new HashSet<string>(entries.Where(e => IsOurs(e, st)).Select(IdOf));
                for (int i = entries.Count - 1; i >= 0; i--) if (ours.Contains(IdOf(entries[i]))) entries.RemoveAt(i);
                string applied = meta["appliedId"]?.GetValue<string>() ?? "";
                // Was die App umlenkt, wird nicht mehr angewendet: unser Eintrag, ein von Hand angelegtes Gateway („Strata“),
                // jeder andere Anbieter als Anthropic. Zurück geht es auf den Anthropic-Eintrag von vorher, wenn es einen gab,
                // sonst auf gar keinen Eintrag (die App meldet sich dann wie gewohnt bei Anthropic an). Fremde Einträge bleiben
                // in der Bibliothek, sie werden nur nicht mehr angewendet.
                if (ours.Contains(applied) || !IsNormal(applied))
                {
                    if (st?.PreviousAppliedId is { Length: > 0 } p && HasEntry(entries, p) && IsNormal(p)) meta["appliedId"] = p;
                    else meta.Remove("appliedId");
                }
                foreach (var id in ours)
                    try { if (File.Exists(EntryPath(id))) File.Delete(EntryPath(id)); } catch { }
                if (st?.CreatedMeta == true && entries.Count == 0)
                {
                    File.Delete(MetaPath);
                    var lib = Path.Combine(_dir, "configLibrary");
                    if (!Directory.EnumerateFileSystemEntries(lib).Any()) Directory.Delete(lib);
                }
                else AtomicFile.WriteAllText(MetaPath, meta.ToJsonString(Pretty));
            }
            DeleteState();
            lock (_lock) _enabled = false;
            Log("after switch off");
            return Strings.ClaudeDisabled;
        }
        catch (Exception ex) { error = Strings.ClaudeFailed(ex.Message); AppLog.Write("claude desktop switch off failed: " + ex); return ""; }
    }

    // Ein Eintrag von uns: der aus der Merkdatei, oder einer mit unserem Namen (ohne Merkdatei)
    private static bool IsOurs(JsonNode? entry, State? st) =>
        st is { EntryId.Length: > 0 } ? IdOf(entry) == st.EntryId || NameOf(entry) == EntryName : NameOf(entry) == EntryName;

    // „Normal“ heißt: die App spricht selbst mit Anthropic. Das ist sie, wenn kein Eintrag angewendet ist, oder wenn der
    // angewendete Eintrag als Anbieter Anthropic nennt. Alles andere (ein Gateway auf diesem Rechner wie Stykker oder
    // ein von Hand angelegtes „Strata“, ein fremdes Gateway, Bedrock, Vertex …) lenkt die App um. Ein angewendeter
    // Eintrag ohne lesbare Datei zählt als umgelenkt: dann hilft nur, ihn nicht mehr anzuwenden.
    private bool IsNormal(string id)
    {
        if (id.Length == 0) return true;
        try
        {
            if (!File.Exists(EntryPath(id)) || JsonNode.Parse(File.ReadAllText(EntryPath(id))) is not JsonObject o) return false;
            var provider = (string?)o["inferenceProvider"] ?? "";
            return provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase) || provider.Equals("firstParty", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Für das Log: Anbieter und Ziel eines Eintrags, nie Schlüssel
    private string TargetOf(string id)
    {
        try
        {
            if (!File.Exists(EntryPath(id)) || JsonNode.Parse(File.ReadAllText(EntryPath(id))) is not JsonObject o) return "?";
            var url = (string?)o["inferenceGatewayBaseUrl"];
            var shown = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "";
            return (string?)o["inferenceProvider"] + (shown.Length > 0 ? " " + shown : "");
        }
        catch { return "?"; }
    }

    private static string NameOf(JsonNode? n) => n is JsonObject o ? o["name"]?.GetValue<string>() ?? "" : "";

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
        AppLog.Write($"claude desktop close: {closed} closed{(error == null ? "" : ", error: " + error)}");
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
            // Nur die Basisadresse: die App hängt /v1/messages selbst an (mit der vollen Adresse fragte sie
            // .../v1/messages/v1/messages an und meldete „Gateway rejected model“)
            ["inferenceGatewayBaseUrl"] = BaseUrl,
            ["inferenceCredentialKind"] = "static",
            ["inferenceGatewayApiKey"] = ApiKey,
            ["inferenceGatewayAuthScheme"] = "bearer",
            // Keine feste Modellliste: die App holt beim Start alle Modelle des Proxys (/v1/models) – lokale Server,
            // Hosts und gewählte Cloud-Modelle. Der erste Eintrag dort ist claude-stykker (das aktuelle Ziel des Proxys).
            ["modelDiscoveryEnabled"] = true,
        };
        return o.ToJsonString(Pretty);
    }

    private bool CurrentEnabled()
    {
        if (!Supported) return false;
        var meta = ReadMeta();
        var applied = meta["appliedId"]?.GetValue<string>() ?? "";
        if (applied.Length == 0) return false;
        var st = ReadState();
        if (st is { Enabled: true } && st.EntryId.Length > 0 && applied == st.EntryId) return true;
        // Sonst: alles, was die App von Anthropic weglenkt, zählt als eingeschaltet (auch von Hand angelegt)
        return !IsNormal(applied);
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

    private sealed record State(bool Enabled, string EntryId, string? PreviousAppliedId, bool CreatedMeta);

    private State? ReadState()
    {
        try
        {
            if (!File.Exists(_stateFile) || JsonNode.Parse(File.ReadAllText(_stateFile)) is not JsonObject o) return null;
            return new State(o["enabled"]?.GetValue<bool>() == true, o["entryId"]?.GetValue<string>() ?? "", o["previousAppliedId"]?.GetValue<string>(),
                o["createdMeta"]?.GetValue<bool>() == true);
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
