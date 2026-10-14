using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerStarter.Core.Config;

// starter.json: die Programme und die Einstellungen. Keine Geheimnisse, keine Laufzeitwerte (PIDs, Status).
public sealed class StarterConfig
{
    public int SchemaVersion { get; set; } = 1;
    public StarterSettings Settings { get; set; } = new();
    public List<ProgramEntry> Programs { get; set; } = new();

    public ProgramEntry? Find(string id) => Programs.FirstOrDefault(p => p.Id == id);
}

public sealed class StarterSettings
{
    public int CheckIntervalMinutes { get; set; } = 10;
    public bool StartWithWindows { get; set; }
    public bool AskBeforeUpdate { get; set; } = true;
    public bool UpdateStoppedWithoutAsking { get; set; }
    public int VersionsKept { get; set; } = 2;
}

public sealed class ProgramEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceExe { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool StartWithStarter { get; set; } = true;
    public bool UpdateWithoutAsking { get; set; }
    public bool KeepPrevious { get; set; } = true;

    // Der Ordner (Name unter <Programm>\) der Kopie, mit der das Programm läuft
    public string? CurrentFolder { get; set; }

    // Alle Kopien, die noch auf der Platte liegen (für Rücksprung und Aufräumen)
    public List<CopyRecord> Copies { get; set; } = new();

    [JsonIgnore]
    public CopyRecord? Current => Copies.FirstOrDefault(c => c.Folder == CurrentFolder);
}

public sealed class CopyRecord
{
    public string Folder { get; set; } = "";
    public string Version { get; set; } = "";
    public string ListHash { get; set; } = "";     // SHA-256 der Dateiliste der Quelle zur Zeit des Kopierens
    public DateTime CopiedUtc { get; set; }
}

// Liest und schreibt starter.json. Schreiben über eine Datei daneben und ein Ersetzen: ein Absturz beim Schreiben
// lässt die alte Datei stehen.
public static class ConfigStore
{
    // camelCase in der Datei; beim Lesen egal, ob Groß- oder Kleinschreibung (eine von Hand geänderte Datei bleibt
    // gültig, und ein Name, der nicht passt, darf nicht still auf den Standard fallen und die Datei überschreiben)
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static StarterConfig Load(DataLayout layout)
    {
        if (!File.Exists(layout.ConfigFile)) return new StarterConfig();
        var json = File.ReadAllText(layout.ConfigFile);
        return JsonSerializer.Deserialize<StarterConfig>(json, Options)
            ?? throw new InvalidDataException($"{DataLayout.ConfigFileName} is empty or not valid");
    }

    public static void Save(DataLayout layout, StarterConfig config)
    {
        Directory.CreateDirectory(layout.Root);
        var temp = layout.ConfigFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, layout.ConfigFile, overwrite: true);
    }
}
