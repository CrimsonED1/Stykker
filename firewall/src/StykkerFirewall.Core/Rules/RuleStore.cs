using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerFirewall.Core.Rules;

// Die Regeln und der Modus von StykkerFirewall als JSON-Datei. Das ist die Wahrheit; das Backend wird danach
// abgeglichen. Schreiben geht über eine Temp-Datei und Umbenennen, damit ein Absturz keine halbe Datei hinterlässt.
public sealed class RuleStore
{
    private sealed record FileModel(ProtectionMode Mode, List<FirewallRule> Rules);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<FirewallRule> _rules = [];

    public RuleStore(string path)
    {
        _path = path;
        Load();
    }

    public string FilePath => _path;

    public ProtectionMode Mode { get; private set; } = ProtectionMode.Monitor;

    public IReadOnlyList<FirewallRule> Rules
    {
        get { lock (_gate) return _rules.ToList(); }
    }

    public void SetMode(ProtectionMode mode)
    {
        lock (_gate) { Mode = mode; Save(); }
    }

    // Neue Regel. Eine bestehende Regel für dieselbe App, Richtung und dasselbe Ziel wird ersetzt (sonst sammeln
    // sich nach „Immer blockieren“ und später „Alles erlauben“ widersprüchliche Regeln an). Gibt die ersetzten Ids zurück.
    public IReadOnlyList<Guid> Add(FirewallRule rule)
    {
        lock (_gate)
        {
            var replaced = _rules.Where(r => SameScope(r, rule)).Select(r => r.Id).ToList();
            _rules.RemoveAll(r => replaced.Contains(r.Id));
            _rules.Add(rule);
            Save();
            return replaced;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            bool removed = _rules.RemoveAll(r => r.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public void RemoveMany(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return;
        lock (_gate)
        {
            if (_rules.RemoveAll(r => set.Contains(r.Id)) > 0) Save();
        }
    }

    private static bool SameScope(FirewallRule a, FirewallRule b) =>
        AppPaths.Same(a.AppPath, b.AppPath) && a.Direction == b.Direction &&
        string.Equals(a.RemoteAddress, b.RemoteAddress, StringComparison.OrdinalIgnoreCase) &&
        a.RemotePort == b.RemotePort && a.LocalPort == b.LocalPort && a.Protocol == b.Protocol;

    private void Load()
    {
        if (!File.Exists(_path)) return;
        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json);
        if (model is null) return;
        Mode = model.Mode;
        _rules.AddRange(model.Rules ?? []);
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel(Mode, _rules), Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
