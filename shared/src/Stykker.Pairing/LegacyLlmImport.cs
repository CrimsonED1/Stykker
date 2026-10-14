using System.Text;
using System.Text.Json;

namespace Stykker.Pairing;

// Übernimmt die Kopplung, die StykkerLLM vor der zentralen Ablage in seinem Datenordner hielt:
//   access.dat  Geräte (Hash, Rolle), Zugangscode, Netzschalter
//   hosts.dat   Model-Hosts (Token-Hash)
// Die Hashes werden unverändert übernommen: ein Gerät bleibt mit seinem alten Cookie angemeldet, ein Host verbindet
// sich mit seinem alten Token. Die alten Dateien werden nur gelesen; was mit ihnen geschieht, entscheidet der Aufrufer.
public static class LegacyLlmImport
{
    public const string Module = "llm";
    public const string AccessFile = "access.dat";
    public const string HostsFile = "hosts.dat";
    // Merker in der Ablage (PairingState.Imported): welche alte Datei übernommen ist
    public const string AccessSource = "llm:access";
    public const string HostsSource = "llm:hosts";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public sealed record Result(int Devices, int Hosts)
    {
        public bool Any => Devices > 0 || Hosts > 0;
    }

    // Liest beide Dateien aus dem Datenordner von StykkerLLM und trägt ihren Inhalt in die Ablage ein. Jede Datei
    // wird genau einmal übernommen: ein später zentral entferntes Gerät kommt nicht aus der alten Datei zurück.
    public static Result ImportFromFolder(PairingStore store, string llmDataDir, IDataProtector? legacyProtector = null)
    {
        legacyProtector ??= DataProtectors.ForCurrentUser(DataProtectors.LegacyLlmEntropy);
        var access = ReadFile(Path.Combine(llmDataDir, AccessFile), legacyProtector);
        var hosts = ReadFile(Path.Combine(llmDataDir, HostsFile), legacyProtector);
        if (access == null && hosts == null) return new Result(0, 0);

        return store.Update(state =>
        {
            int devices = 0, hostCount = 0;
            bool changed = false;
            if (access != null && !state.Imported.Contains(AccessSource))
            {
                devices = MergeAccess(state, access);
                state.Imported.Add(AccessSource);
                changed = true;
            }
            if (hosts != null && !state.Imported.Contains(HostsSource))
            {
                hostCount = MergeHosts(state, hosts);
                state.Imported.Add(HostsSource);
                changed = true;
            }
            return (changed, new Result(devices, hostCount));
        });
    }

    // Der JSON-Inhalt einer Datei aus dem alten Datenordner: Klartext, oder Base64 eines DPAPI-Blobs.
    // null = fehlt oder nicht lesbar.
    public static string? ReadFile(string path, IDataProtector legacyProtector)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path).Trim();
            if (raw.StartsWith('{')) return raw;
            var plain = legacyProtector.Unprotect(Convert.FromBase64String(raw));
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    // Geräte, Code und Netzschalter aus dem JSON einer access.dat. Liefert die Zahl der übernommenen Geräte.
    public static int MergeAccess(PairingState state, string accessJson)
    {
        LegacyAccess? legacy;
        try { legacy = JsonSerializer.Deserialize<LegacyAccess>(accessJson, Json); }
        catch (JsonException) { return 0; }
        if (legacy == null) return 0;

        // Ein eingeschalteter Netzschalter bleibt eingeschaltet: sonst sind die Geräte im Heimnetz ausgesperrt
        state.RemoteEnabled |= legacy.RemoteEnabled;
        if (!PairingCodes.IsCode(state.Code) && PairingCodes.IsCode(legacy.Code?.Trim()))
        {
            state.Code = legacy.Code!.Trim();
            state.CodeCreated = legacy.CodeCreated.ToUniversalTime();
            state.CodeWrongTries = 0;
        }

        int added = 0;
        foreach (var old in legacy.Devices ?? new())
        {
            if (string.IsNullOrEmpty(old.Id) || string.IsNullOrEmpty(old.Hash)) continue;
            if (state.Devices.Any(d => d.Hash == old.Hash)) continue;
            state.Devices.Add(new PairedDevice
            {
                Id = UniqueId(state, old.Id),
                Name = old.Name ?? "",
                Kind = DeviceKinds.Browser,
                Created = old.Created.ToUniversalTime(),
                LastSeen = old.LastSeen?.ToUniversalTime(),
                Address = old.Address,
                Hash = old.Hash,
                Roles = { [Module] = PairingRoles.NormalizeLegacy(old.Role) },
            });
            added++;
        }
        return added;
    }

    // Hosts aus dem JSON einer hosts.dat. Liefert die Zahl der übernommenen Hosts.
    public static int MergeHosts(PairingState state, string hostsJson)
    {
        LegacyHosts? legacy;
        try { legacy = JsonSerializer.Deserialize<LegacyHosts>(hostsJson, Json); }
        catch (JsonException) { return 0; }

        int added = 0;
        foreach (var old in legacy?.Hosts ?? new())
        {
            if (string.IsNullOrEmpty(old.Id) || string.IsNullOrEmpty(old.TokenHash)) continue;
            if (state.Devices.Any(d => d.Hash == old.TokenHash)) continue;
            state.Devices.Add(new PairedDevice
            {
                Id = UniqueId(state, old.Id),
                Name = old.Name ?? "host",
                Kind = DeviceKinds.Host,
                Created = old.Added.ToUniversalTime(),
                Hash = old.TokenHash,
            });
            added++;
        }
        return added;
    }

    // Die alte Id bleibt, solange sie frei ist: Verweise aus der Oberfläche (Geräteliste) zeigen weiter aufs selbe Gerät
    private static string UniqueId(PairingState state, string id) =>
        state.Devices.Any(d => d.Id == id) ? Guid.NewGuid().ToString("N")[..12] : id;

    // Die Form der alten Dateien (StykkerLlm.Core: AccessState, AccessDevice, HostFile, HostEntry)
    private sealed class LegacyAccess
    {
        public bool RemoteEnabled { get; set; }
        public string? Code { get; set; }
        public DateTime CodeCreated { get; set; }
        public List<LegacyDevice>? Devices { get; set; }
    }

    private sealed class LegacyDevice
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastSeen { get; set; }
        public string? Address { get; set; }
        public string? Role { get; set; }
        public string? Hash { get; set; }
    }

    private sealed class LegacyHosts
    {
        public List<LegacyHost>? Hosts { get; set; }
    }

    private sealed class LegacyHost
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? TokenHash { get; set; }
        public DateTime Added { get; set; }
    }
}
