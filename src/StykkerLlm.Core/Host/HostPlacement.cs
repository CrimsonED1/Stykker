using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Eine Modelldatei auf einem Host (Antwort auf den Befehl „models“)
public sealed record HostModelFile(string Path, string Name, double SizeGb);

// Was die Platzierung über einen Host weiß: seine Modelldateien und der freie Grafikspeicher (null = unbekannt)
public sealed record HostCandidate(string HostId, string Name, IReadOnlyList<HostModelFile> Files, double? FreeVramGb);

// Ergebnis: Host und Datei, oder warum es keinen gibt (NoHostHasIt: niemand hat die Datei, dann gilt das Standardziel)
public sealed record HostChoice(string HostId, string HostName, HostModelFile File, string? Error = null, bool NoHostHasIt = false)
{
    public static HostChoice None => new("", "", new HostModelFile("", "", 0), NoHostHasIt: true);
    public static HostChoice Fail(string error) => new("", "", new HostModelFile("", "", 0), error);
    public bool Ok => Error == null && !NoHostHasIt;
}

// Automatische Platzierung (docs/plan-hosts-gateway.md, P6): ein angefragtes Modell, das noch nirgends läuft, startet auf
// dem Host, der die Datei hat und genug Grafikspeicher frei hat (der mit dem meisten freien Speicher gewinnt).
public static class HostPlacement
{
    // Etwas Luft für Kontext und Puffer über der Dateigröße
    public static double NeededGb(double fileGb) => fileGb * 1.15 + 0.5;

    public static bool NameMatches(HostModelFile f, string wanted)
    {
        var w = ServerInfo.ModelName(wanted.Trim());
        return f.Name.Equals(w, StringComparison.OrdinalIgnoreCase)
            || ServerInfo.ModelName(f.Path).Equals(w, StringComparison.OrdinalIgnoreCase);
    }

    public static HostChoice Choose(string wanted, IEnumerable<HostCandidate> hosts)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return HostChoice.None;
        var having = hosts.SelectMany(h => h.Files.Where(f => NameMatches(f, wanted)).Take(1).Select(f => (Host: h, File: f))).ToList();
        if (having.Count == 0) return HostChoice.None;
        var fits = having.Where(x => x.Host.FreeVramGb is not double free || free >= NeededGb(x.File.SizeGb))
            .OrderByDescending(x => x.Host.FreeVramGb ?? -1).ToList();
        if (fits.Count == 0)
        {
            var best = having.OrderByDescending(x => x.Host.FreeVramGb ?? 0).First();
            return HostChoice.Fail(Strings.HostNoVramFor(best.File.Name, NeededGb(best.File.SizeGb), best.Host.FreeVramGb ?? 0));
        }
        var c = fits[0];
        return new HostChoice(c.Host.HostId, c.Host.Name, c.File);
    }

    public static IReadOnlyList<HostModelFile> ParseFiles(JsonElement? data)
    {
        var list = new List<HostModelFile>();
        if (data is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            double size = e.TryGetProperty("sizeGb", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetDouble() : 0;
            if (S("path").Length > 0) list.Add(new HostModelFile(S("path"), S("name").Length > 0 ? S("name") : ServerInfo.ModelName(S("path")), size));
        }
        return list;
    }
}

// Startet Modelle bei Bedarf auf den Hosts. Die Dateilisten der Hosts werden zwischengespeichert (FilesMaxAge); gleichzeitige
// Anfragen nach demselben Modell teilen sich einen Start.
public sealed class HostScheduler(HostHub hub)
{
    public TimeSpan FilesMaxAge { get; set; } = TimeSpan.FromMinutes(5);
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<HostModelFile> Files)> _files = new();
    private readonly ConcurrentDictionary<string, Task<HostChoice>> _starting = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Log;

    // Namen aller Modelldateien der verbundenen Hosts (für /v1/models), nur aus dem Zwischenspeicher
    public IReadOnlyList<string> KnownModels() =>
        _files.Where(kv => hub.Find(kv.Key)?.Connected == true).SelectMany(kv => kv.Value.Files.Select(f => f.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IReadOnlyList<HostModelFile>> FilesAsync(string hostId, bool fresh = false, CancellationToken ct = default)
    {
        if (!fresh && _files.TryGetValue(hostId, out var c) && Now() - c.At < FilesMaxAge) return c.Files;
        var r = await hub.CommandAsync(hostId, "models", null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!r.Ok) return _files.TryGetValue(hostId, out var old) ? old.Files : Array.Empty<HostModelFile>();
        var files = HostPlacement.ParseFiles(r.Data);
        _files[hostId] = (Now(), files);
        return files;
    }

    // Dateilisten im Hintergrund auffrischen (im Takt aufgerufen; fragt nur Hosts, deren Liste veraltet ist)
    public void Refresh()
    {
        foreach (var h in hub.List().Where(h => h.Connected))
        {
            if (_files.TryGetValue(h.Entry.Id, out var c) && Now() - c.At < FilesMaxAge) continue;
            _files[h.Entry.Id] = (Now(), c.Files ?? Array.Empty<HostModelFile>());   // nicht doppelt fragen, solange es läuft
            var id = h.Entry.Id;
            _ = Task.Run(async () =>
            {
                try { await FilesAsync(id, fresh: true).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Log?.Invoke($"hosts: model list of {id}: {ex.Message}"); }
            });
        }
    }

    // Das Modell auf einem passenden Host starten. NoHostHasIt: kein Host hat die Datei (dann gilt das Standardziel).
    public Task<HostChoice> StartAsync(string wanted, CancellationToken ct = default)
    {
        var key = ServerInfo.ModelName(wanted.Trim());
        var task = _starting.GetOrAdd(key, _ => StartCoreAsync(wanted, ct));
        _ = task.ContinueWith(_ => _starting.TryRemove(key, out var _), TaskScheduler.Default);
        return task;
    }

    private async Task<HostChoice> StartCoreAsync(string wanted, CancellationToken ct)
    {
        var cands = new List<HostCandidate>();
        foreach (var h in hub.List().Where(h => h.Connected))
        {
            var files = await FilesAsync(h.Entry.Id, ct: ct).ConfigureAwait(false);
            double? free = h.State?.Gpu is { } g && g.MemTotalGb > 0 ? Math.Max(0, g.MemTotalGb - g.MemUsedGb) : null;
            cands.Add(new HostCandidate(h.Entry.Id, h.Name.Length > 0 ? h.Name : h.Entry.Name, files, free));
        }
        var choice = HostPlacement.Choose(wanted, cands);
        if (!choice.Ok) return choice;
        Log?.Invoke($"hosts: starting {choice.File.Name} on {choice.HostName}");
        var r = await hub.CommandAsync(choice.HostId, "start", new JsonObject { ["model"] = choice.File.Path }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return r.Ok ? choice : HostChoice.Fail(r.Message);
    }
}
