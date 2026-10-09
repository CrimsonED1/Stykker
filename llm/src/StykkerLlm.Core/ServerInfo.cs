namespace StykkerLlm.Core;

// Warum sich ein erkannter Server nicht merken lässt (nur anzeigen)
public enum SaveBlock
{
    None,
    CommandLineUnreadable,   // Prozess gehört einem anderen Benutzer / ist erhöht gestartet
    RouterMode,              // Router-/--models-dir-Modus
    HuggingFace,             // -hf: Modell aus dem Hugging-Face-Cache
    Manual,                  // von Hand hinzugefügt: kein lokaler Prozess
    OtherBackend,            // Ollama / LM Studio: nur lesend überwacht
}

// Ein llama-server, den LM Studio selbst als Kindprozess startet (Engine eines geladenen Modells). Er ist keine eigene Karte
// (Stop/Save würden LM Studio die Engine abschießen), sondern gehört zur LM-Studio-Karte. ApiKey nur für interne Abfragen.
public sealed record ManagedChild(int Pid, long StartTicks, string Host, int Port, SecretValue? ApiKey, string? ModelPath);

// Unveränderliche Beschreibung eines erkannten (oder manuell eingetragenen) Servers. Enthält nie Geheimnisse:
// Kommandozeile und Umgebung sind vor der Ablage geschwärzt (siehe CmdLine.Redact).
public sealed class ServerInfo
{
    public string Key { get; init; } = "";              // Host:Port, stabil über Neustarts
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string Url => NetAddr.Url(Host, Port);
    public int? Pid { get; init; }
    public long StartTicks { get; init; }
    public int ParentPid { get; init; }
    public bool Manual { get; init; }
    public string DetectedBy { get; init; } = "";       // "name", "props" oder "manual"

    public string? Program { get; init; }               // Pfad der exe
    public string? WorkingDir { get; init; }
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();   // ohne Programm, Geheimnisse geschwärzt
    public string? CommandLine { get; init; }           // geschwärzt, zum Anzeigen und Kopieren
    public bool CommandLineReadable { get; init; }
    public bool HasSecrets { get; init; }
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public LlamaServerArgs? Params { get; init; }
    // API-Schlüssel des Servers, nur für interne Anfragen (nie anzeigen, speichern oder kopieren)
    public SecretValue? ApiKey { get; init; }
    public BackendKind Backend { get; init; } = BackendKind.LlamaCpp;
    public string? BackendVersion { get; init; }
    public ServerMode Mode { get; init; } = ServerMode.Normal;
    // LM Studio: die vom Programm gestarteten llama-server (Engines); sonst leer
    public IReadOnlyList<ManagedChild> Children { get; init; } = Array.Empty<ManagedChild>();

    // Aus /props beim Erkennen (nur wenn per Probe erkannt); sonst null
    public LlamaProps? Props { get; init; }

    public string? ModelPath => Params?.Model ?? Props?.ModelPath;
    // --log-file, relativ zum Arbeitsordner aufgelöst
    public string? LogFile
    {
        get
        {
            if (Manual) return string.IsNullOrEmpty(ManualLog) ? null : ManualLog;
            var lf = Params?.LogFile;
            if (string.IsNullOrEmpty(lf)) return null;
            if (Path.IsPathRooted(lf) || string.IsNullOrEmpty(WorkingDir)) return lf;
            return Path.GetFullPath(Path.Combine(WorkingDir, lf));
        }
    }

    public string Name
    {
        get
        {
            if (!string.IsNullOrEmpty(ManualName)) return ManualName;
            if (Backend != BackendKind.LlamaCpp) return BackendProbes.DisplayName(Backend);
            var n = Params?.Alias ?? Props?.ModelAlias;
            if (string.IsNullOrEmpty(n) && ModelPath is { Length: > 0 } mp) n = ModelName(mp);
            if (string.IsNullOrEmpty(n) && Params?.HfRepo is { Length: > 0 } hf) n = hf;
            if (!string.IsNullOrEmpty(n)) return n;
            return Manual ? Key : $"llama-server :{Port}";
        }
    }

    public string? ManualName { get; init; }
    public string? ManualLog { get; init; }

    // Name ist nur der Platzhalter "llama-server :Port" (weder Alias noch Modell bekannt)
    public bool NameIsGeneric => Backend == BackendKind.LlamaCpp && string.IsNullOrEmpty(ManualName) && string.IsNullOrEmpty(Params?.Alias) && string.IsNullOrEmpty(Props?.ModelAlias) &&
                                 string.IsNullOrEmpty(ModelPath) && string.IsNullOrEmpty(Params?.HfRepo) && !Manual;

    public SaveBlock SaveBlock =>
        Manual ? SaveBlock.Manual :
        Backend != BackendKind.LlamaCpp ? SaveBlock.OtherBackend :
        !CommandLineReadable ? SaveBlock.CommandLineUnreadable :
        Mode == ServerMode.Router ? SaveBlock.RouterMode :
        Mode == ServerMode.HuggingFace ? SaveBlock.HuggingFace : SaveBlock.None;

    public bool CanSave => SaveBlock == SaveBlock.None;

    // Dateiname ohne Endung, egal ob der Pfad mit \ oder / geschrieben ist
    public static string ModelName(string path)
    {
        var f = path.Replace('\\', '/');
        f = f[(f.LastIndexOf('/') + 1)..];
        return f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? f[..^5] : f;
    }
}

// Die für die Erkennung wichtigen Felder von GET /props (llama-server)
public sealed record LlamaProps(string? BuildInfo, string? ModelPath, string? ModelAlias, int TotalSlots, int NCtx, bool IsSleeping, string? Role, string? ModelFtype)
{
    public static LlamaProps? Parse(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            int I(System.Text.Json.JsonElement e, string n) =>
                e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
            int nctx = r.TryGetProperty("default_generation_settings", out var dg) ? I(dg, "n_ctx") : 0;
            bool sleeping = r.TryGetProperty("is_sleeping", out var sl) && sl.ValueKind == System.Text.Json.JsonValueKind.True;
            // Merkmale eines llama-server: build_info (Text), model_path (Text), total_slots oder default_generation_settings
            bool looks = S("build_info") != null || S("model_path") != null || r.TryGetProperty("total_slots", out _) ||
                         r.TryGetProperty("default_generation_settings", out _) || S("role") == "router";
            return looks ? new LlamaProps(S("build_info"), S("model_path"), S("model_alias"), I(r, "total_slots"), nctx, sleeping, S("role"), S("model_ftype")) : null;
        }
        catch { return null; }
    }
}
