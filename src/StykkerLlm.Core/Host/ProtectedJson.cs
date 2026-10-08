using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// JSON in einer Datei, an den Windows-Benutzer gebunden (DPAPI) wo es geht – sonst als Klartext (Linux).
// Fehler beim Lesen ergeben den Standardwert, Fehler beim Schreiben gehen verloren (dann gilt es nur für diese Sitzung).
public static class ProtectedJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    public static T Load<T>(string file, IPlatform platform) where T : new()
    {
        try
        {
            if (!File.Exists(file)) return new T();
            var raw = File.ReadAllText(file).Trim();
            string json;
            if (raw.StartsWith("{", StringComparison.Ordinal)) json = raw;
            else
            {
                var blob = Convert.FromBase64String(raw);
                json = Encoding.UTF8.GetString(platform.UnprotectForCurrentUser(blob) ?? blob);
            }
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException) { return new T(); }
    }

    public static void Save<T>(string file, T value, IPlatform platform)
    {
        var json = JsonSerializer.Serialize(value, Options);
        try
        {
            var protectedBytes = platform.ProtectForCurrentUser(Encoding.UTF8.GetBytes(json));
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(file, protectedBytes == null ? json : Convert.ToBase64String(protectedBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
