using System.Globalization;

namespace StykkerLlm.Core;

// Ein Parameter der Kommandozeile so, wie er geschrieben wurde. Name leer = Positionsargument.
public sealed record ArgEntry(string Name, string? Value, bool Inline)
{
    public IEnumerable<string> Tokens()
    {
        if (Name.Length == 0) { if (Value != null) yield return Value; yield break; }
        if (Value == null) { yield return Name; yield break; }
        if (Inline) yield return Name + "=" + Value;
        else { yield return Name; yield return Value; }
    }
}

public enum ServerMode
{
    Normal,        // ein Modell per -m: lässt sich merken und wieder starten
    Router,        // kein -m, kein -hf (Router-Modus / --models-dir / --models-preset): nur anzeigen
    HuggingFace,   // -hf: Modell kommt aus dem Cache/Netz: nur anzeigen
}

// Parser für die Kommandozeile von llama-server. Unbekannte Parameter bleiben unverändert erhalten
// (ToArgs liefert die ursprüngliche Reihenfolge). Bei mehrfach gesetzten Parametern gewinnt der letzte.
public sealed class LlamaServerArgs
{
    private enum Kind { Flag, Value, OptionalSwitch }

    // Schreibweise -> (kanonischer Name, Art). Nicht gelistete Parameter gelten als unbekannt.
    private static readonly Dictionary<string, (string Canon, Kind Kind)> Known = BuildTable();

    private static Dictionary<string, (string, Kind)> BuildTable()
    {
        var t = new Dictionary<string, (string, Kind)>(StringComparer.Ordinal);
        void V(string canon, params string[] names) { foreach (var n in names) t[n] = (canon, Kind.Value); }
        void F(string canon, params string[] names) { foreach (var n in names) t[n] = (canon, Kind.Flag); }
        V("model", "-m", "--model");
        V("port", "--port");
        V("host", "--host");
        V("alias", "-a", "--alias");
        V("ctx", "-c", "--ctx-size");
        V("ngl", "-ngl", "--gpu-layers", "--n-gpu-layers");
        V("np", "-np", "--parallel");
        V("ctk", "-ctk", "--cache-type-k");
        V("ctv", "-ctv", "--cache-type-v");
        V("temp", "--temp", "--temperature");
        V("top-p", "--top-p");
        V("top-k", "--top-k");
        V("min-p", "--min-p");
        V("log-file", "--log-file");
        V("api-key", "--api-key");
        V("api-key-file", "--api-key-file");
        V("hf-token", "-hft", "--hf-token");
        V("hf-repo", "-hf", "-hfr", "--hf-repo");
        V("hf-file", "-hff", "--hf-file");
        V("models-dir", "--models-dir");
        V("models-preset", "--models-preset");
        V("models-max", "--models-max");
        V("threads", "-t", "--threads");
        V("threads-batch", "-tb", "--threads-batch");
        V("batch", "-b", "--batch-size");
        V("ubatch", "-ub", "--ubatch-size");
        V("chat-template", "--chat-template");
        V("chat-template-file", "--chat-template-file");
        V("split-mode", "-sm", "--split-mode");
        V("tensor-split", "-ts", "--tensor-split");
        V("main-gpu", "-mg", "--main-gpu");
        V("predict", "-n", "--predict", "--n-predict");
        V("rope-scaling", "--rope-scaling");
        V("rope-freq-base", "--rope-freq-base");
        V("rope-freq-scale", "--rope-freq-scale");
        V("reasoning-format", "--reasoning-format");
        V("reasoning-budget", "--reasoning-budget");
        V("sleep-idle-seconds", "--sleep-idle-seconds");
        V("timeout", "-to", "--timeout");
        V("path", "--path");
        V("lora", "--lora");
        V("mmproj", "-mm", "--mmproj");
        V("override-tensor", "-ot", "--override-tensor");
        F("jinja", "--jinja");
        F("no-jinja", "--no-jinja");
        F("mlock", "--mlock");
        F("no-mmap", "--no-mmap");
        F("cont-batching", "-cb", "--cont-batching");
        F("no-cont-batching", "-nocb", "--no-cont-batching");
        F("no-webui", "--no-webui");
        F("embedding", "--embedding", "--embeddings");
        F("reranking", "--reranking", "--rerank");
        F("metrics", "--metrics");
        F("slots", "--slots");
        F("no-slots", "--no-slots");
        F("props", "--props");
        F("verbose", "-v", "--verbose");
        F("log-disable", "--log-disable");
        F("log-timestamps", "--log-timestamps");
        F("log-prefix", "--log-prefix");
        F("context-shift", "--context-shift");
        F("no-context-shift", "--no-context-shift");
        F("swa-full", "--swa-full");
        F("kv-unified", "-kvu", "--kv-unified");
        F("no-kv-offload", "-nkvo", "--no-kv-offload");
        F("no-warmup", "--no-warmup");
        F("no-mmproj", "--no-mmproj");
        // -fa: neuere Versionen on|off|auto, ältere nur als Schalter
        t["-fa"] = ("flash-attn", Kind.OptionalSwitch);
        t["--flash-attn"] = ("flash-attn", Kind.OptionalSwitch);
        return t;
    }

    private static readonly HashSet<string> SwitchValues = new(StringComparer.OrdinalIgnoreCase) { "on", "off", "auto", "true", "false", "1", "0" };

    // Umgebungsvariablen, die llama-server als Vorgabe für einen fehlenden Parameter liest
    private static readonly Dictionary<string, string> EnvNames = new()
    {
        ["model"] = "LLAMA_ARG_MODEL", ["port"] = "LLAMA_ARG_PORT", ["host"] = "LLAMA_ARG_HOST", ["alias"] = "LLAMA_ARG_ALIAS",
        ["ctx"] = "LLAMA_ARG_CTX_SIZE", ["ngl"] = "LLAMA_ARG_N_GPU_LAYERS", ["np"] = "LLAMA_ARG_N_PARALLEL",
        ["ctk"] = "LLAMA_ARG_CACHE_TYPE_K", ["ctv"] = "LLAMA_ARG_CACHE_TYPE_V", ["flash-attn"] = "LLAMA_ARG_FLASH_ATTN",
        ["hf-repo"] = "LLAMA_ARG_HF_REPO", ["models-dir"] = "LLAMA_ARG_MODELS_DIR",
    };

    public IReadOnlyList<ArgEntry> Entries { get; }
    private readonly IReadOnlyDictionary<string, string>? _env;

    private LlamaServerArgs(List<ArgEntry> entries, IReadOnlyDictionary<string, string>? env)
    {
        Entries = entries;
        _env = env;
    }

    private static bool LooksLikeOption(string s) =>
        s.Length > 1 && s[0] == '-' && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    // args: ohne Programmname. env: (gefilterte) Umgebung des Prozesses als Vorgabewerte, optional.
    public static LlamaServerArgs Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var entries = new List<ArgEntry>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!LooksLikeOption(a)) { entries.Add(new ArgEntry("", a, false)); continue; }

            string name = a;
            int eq = a.StartsWith("--", StringComparison.Ordinal) ? a.IndexOf('=') : -1;
            if (eq > 0)
            {
                entries.Add(new ArgEntry(a[..eq], a[(eq + 1)..], true));
                continue;
            }

            bool hasNext = i + 1 < args.Count;
            if (Known.TryGetValue(name, out var k))
            {
                switch (k.Kind)
                {
                    case Kind.Flag:
                        entries.Add(new ArgEntry(name, null, false));
                        break;
                    case Kind.Value:
                        entries.Add(new ArgEntry(name, hasNext ? args[++i] : null, false));
                        break;
                    default:   // OptionalSwitch
                        entries.Add(new ArgEntry(name, hasNext && SwitchValues.Contains(args[i + 1]) ? args[++i] : null, false));
                        break;
                }
            }
            else
            {
                // unbekannt: folgt etwas, das nicht wie ein Parameter aussieht, gilt es als Wert
                entries.Add(new ArgEntry(name, hasNext && !LooksLikeOption(args[i + 1]) ? args[++i] : null, false));
            }
        }
        return new LlamaServerArgs(entries, env);
    }

    // Die ursprünglichen Argumente in der ursprünglichen Reihenfolge
    public List<string> ToArgs() => Entries.SelectMany(e => e.Tokens()).ToList();

    private static string Canon(string name) => Known.TryGetValue(name, out var k) ? k.Canon : name;

    // Letzter Wert zu einem kanonischen Namen (oder einer Schreibweise wie "--port"); null = nicht gesetzt.
    // Ein Schalter ohne Wert ergibt "". Fehlt der Parameter, gilt die passende LLAMA_ARG_*-Umgebungsvariable.
    public string? Get(string canonicalOrName)
    {
        var c = Canon(canonicalOrName);
        string? found = null;
        bool any = false;
        foreach (var e in Entries)
        {
            if (e.Name.Length == 0 || Canon(e.Name) != c) continue;
            any = true;
            found = e.Value ?? "";
        }
        if (any) return found;
        if (_env != null && EnvNames.TryGetValue(c, out var envName) && _env.TryGetValue(envName, out var ev) && ev.Length > 0) return ev;
        return null;
    }

    public bool Has(string canonicalOrName)
    {
        var c = Canon(canonicalOrName);
        return Entries.Any(e => e.Name.Length > 0 && Canon(e.Name) == c);
    }

    private int? GetInt(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private double? GetDouble(string name) => double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public string? Model => NullIfEmpty(Get("model"));
    public string? Host => NullIfEmpty(Get("host"));
    public string? Alias => NullIfEmpty(Get("alias"));
    public string? LogFile => NullIfEmpty(Get("log-file"));
    public string? CacheTypeK => NullIfEmpty(Get("ctk"));
    public string? CacheTypeV => NullIfEmpty(Get("ctv"));
    public string? HfRepo => NullIfEmpty(Get("hf-repo"));
    public string? ModelsDir => NullIfEmpty(Get("models-dir"));
    public string? ModelsPreset => NullIfEmpty(Get("models-preset"));
    public int? Port => GetInt("port");
    public int? Ctx => GetInt("ctx");
    public int? Np => GetInt("np");
    public string? NglRaw => NullIfEmpty(Get("ngl"));
    public int? Ngl => GetInt("ngl");
    public int? TopK => GetInt("top-k");
    public double? Temp => GetDouble("temp");
    public double? TopP => GetDouble("top-p");
    public double? MinP => GetDouble("min-p");

    // null = nicht gesetzt; ein Schalter ohne Wert zählt als "an"
    public bool? FlashAttn => Get("flash-attn") is { } f ? !(f.Equals("off", StringComparison.OrdinalIgnoreCase) || f is "0" or "false") : null;

    // --jinja / --no-jinja: das zuletzt genannte gewinnt; null = keins von beiden
    public bool? Jinja
    {
        get
        {
            bool? r = null;
            foreach (var e in Entries)
                if (e.Name == "--jinja") r = true;
                else if (e.Name == "--no-jinja") r = false;
            return r;
        }
    }

    public ServerMode Mode => HfRepo != null ? ServerMode.HuggingFace : Model == null ? ServerMode.Router : ServerMode.Normal;
}
