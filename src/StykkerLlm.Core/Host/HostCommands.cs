using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Einstellungen des Hosts (host-settings.json im Datenordner des Hosts): wo Modelle liegen und welche Programme er
// zusätzlich zu den bekannten Modellservern starten darf.
public sealed class HostSettings
{
    public const string FileName = "host-settings.json";
    public List<string> ModelRoots { get; set; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"),
    };
    public List<string> AllowPrograms { get; set; } = new();

    public static HostSettings Load(AppPaths paths)
    {
        var file = Path.Combine(paths.Root, FileName);
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        var fresh = new HostSettings();
        try { AtomicFile.WriteAllText(file, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return fresh;
    }

    // Bekannte Modellserver: llama.cpp (llama-server, auch Varianten wie llama-server-cuda), Ollama, LM Studio (lms),
    // vLLM, koboldcpp. Alles andere nur, wenn es in AllowPrograms steht (Name oder voller Pfad).
    private static readonly string[] Known = { "llama-server", "ollama", "lms", "vllm", "koboldcpp" };

    public bool Allows(string program)
    {
        if (string.IsNullOrWhiteSpace(program)) return false;
        var name = Path.GetFileNameWithoutExtension(program.Trim().Trim('"')).ToLowerInvariant();
        if (Known.Any(k => name == k || name.StartsWith(k + "-", StringComparison.Ordinal) || name.StartsWith(k + "_", StringComparison.Ordinal))) return true;
        return AllowPrograms.Any(a => a.Equals(program, StringComparison.OrdinalIgnoreCase)
                                      || Path.GetFileNameWithoutExtension(a).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

// Befehle des Servers auf dem Host ausführen (docs/plan-hosts-gateway.md, P4). Der Server ist gekoppelt und damit
// befugt – Rückfragen beantwortet RemotePrompt mit Ja. Gestartet werden nur Modellserver (HostSettings.Allows).
//   models                         GGUF-Dateien in den Modellordnern: [{ path, name, sizeGb }]
//   start  { name, program, args, workingDir }    Modellserver starten
//   stop   { key }                 einen laufenden Server stoppen
//   unload { key, model }          ein Modell entladen (Ollama, LM Studio)
public sealed class HostCommands(MonitorEngine engine, HostSettings settings)
{
    public async Task<HostReply> RunAsync(string name, JsonElement args, CancellationToken ct)
    {
        switch (name)
        {
            case "models":
            {
                var found = await Task.Run(() => GgufScan.Find(settings.ModelRoots, ScanCancellation: ct), ct).ConfigureAwait(false);
                var list = new JsonArray();
                foreach (var g in found)
                    list.Add(new JsonObject { ["path"] = g.Path, ["name"] = ServerInfo.ModelName(g.Path), ["sizeGb"] = Math.Round(g.FileSize / 1e9, 2) });
                return new HostReply(true, Strings.HostModelsFound(found.Count), JsonSerializer.SerializeToElement(list));
            }
            case "start":
            {
                var program = Str(args, "program");
                if (!settings.Allows(program)) return new HostReply(false, Strings.HostProgramNotAllowed(program));
                var argv = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                var work = Str(args, "workingDir");
                var spec = new LaunchSpec(Str(args, "name") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(program), program, argv, work.Length > 0 ? work : null);
                var prompt = new RemotePrompt();
                bool ok = await new LaunchCoordinator(engine, prompt).StartSpecAsync(spec).ConfigureAwait(false);
                return new HostReply(ok, ok ? Strings.HostStarted(spec.Name) : string.Join(" ", prompt.Messages));
            }
            case "stop":
            {
                var s = engine.Servers.FirstOrDefault(x => x.Key == Str(args, "key"));
                if (s == null) return new HostReply(false, Strings.HostNoSuchServer);
                var prompt = new RemotePrompt();
                await new LaunchCoordinator(engine, prompt).StopServerAsync(s).ConfigureAwait(false);
                return new HostReply(true, prompt.Messages.LastOrDefault() ?? Strings.HostStopped(s.Name));
            }
            case "unload":
            {
                var s = engine.Servers.FirstOrDefault(x => x.Key == Str(args, "key"));
                if (s == null) return new HostReply(false, Strings.HostNoSuchServer);
                bool ok = await s.UnloadAsync(Str(args, "model")).ConfigureAwait(false);
                return new HostReply(ok, ok ? Strings.HostUnloaded(Str(args, "model")) : Strings.UnloadFailed(Str(args, "model")));
            }
            default:
                return new HostReply(false, Strings.HostCommandUnknown(name));
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
