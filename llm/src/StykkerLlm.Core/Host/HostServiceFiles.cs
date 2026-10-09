using System.Text.Json;

namespace StykkerLlm.Core;

// StykkerHost als Windows-Dienst (docs/plan-hosts-gateway.md, P8). Der Dienst läuft als LocalSystem mit eigenem Datenordner
// (ProgramData). Sein Token schützt er mit seinem eigenen DPAPI-Schlüssel – deshalb koppelt er selbst: „StykkerHost
// pair-service“ (als Administrator) legt eine Anfrage in den Datenordner, der Dienst führt sie aus und legt das Ergebnis daneben.
public static class HostServiceFiles
{
    public const string ServiceName = "StykkerHost";
    public const string RequestFile = "pair-request.json", ResultFile = "pair-result.json";

    // ProgramData\StykkerLLM\host (der Dienst); der Tray-Host nutzt weiter den Ordner des Benutzers
    public static AppPaths ServicePaths() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StykkerLLM", "host"));

    public sealed record Request(string Server = "", string Code = "", bool Unpair = false);
    public sealed record Result(bool Ok, string Message);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static void WriteRequest(AppPaths paths, Request req)
    {
        Directory.CreateDirectory(paths.Root);
        try { File.Delete(Path.Combine(paths.Root, ResultFile)); } catch (IOException) { }
        AtomicFile.WriteAllText(Path.Combine(paths.Root, RequestFile), JsonSerializer.Serialize(req));
    }

    // Für den Dienst: eine offene Anfrage holen und dabei löschen (der Code ist nur einmal gültig)
    public static Request? TakeRequest(AppPaths paths)
    {
        var file = Path.Combine(paths.Root, RequestFile);
        if (!File.Exists(file)) return null;
        try
        {
            var text = File.ReadAllText(file);
            File.Delete(file);
            return JsonSerializer.Deserialize<Request>(text, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void WriteResult(AppPaths paths, Result result) =>
        AtomicFile.WriteAllText(Path.Combine(paths.Root, ResultFile), JsonSerializer.Serialize(result));

    public static async Task<Result?> WaitForResultAsync(AppPaths paths, TimeSpan timeout, CancellationToken ct = default)
    {
        var file = Path.Combine(paths.Root, ResultFile);
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try
            {
                if (File.Exists(file))
                {
                    var r = JsonSerializer.Deserialize<Result>(File.ReadAllText(file), Options);
                    File.Delete(file);
                    return r;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return null;
    }

    // Argumente für sc.exe (eigene Liste je Aufruf): anlegen mit verzögertem Autostart, Beschreibung, Neustart bei Absturz
    public static IReadOnlyList<string[]> InstallCommands(string exe) => new[]
    {
        new[] { "create", ServiceName, "binPath=", $"\"{exe}\" --service", "start=", "delayed-auto", "DisplayName=", ServiceName },
        new[] { "description", ServiceName, Strings.HostServiceDescription },
        new[] { "failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000" },
        new[] { "start", ServiceName },
    };

    public static IReadOnlyList<string[]> UninstallCommands() => new[]
    {
        new[] { "stop", ServiceName },
        new[] { "delete", ServiceName },
    };

    // Die Modellordner des installierenden Benutzers übernehmen: der Dienst (LocalSystem) hat kein eigenes Benutzerprofil
    // mit Modellen. Gibt es schon Einstellungen des Dienstes, bleiben sie.
    public static void SeedSettings(AppPaths userPaths, AppPaths servicePaths)
    {
        var target = Path.Combine(servicePaths.Root, HostSettings.FileName);
        if (File.Exists(target)) return;
        Directory.CreateDirectory(servicePaths.Root);
        var source = Path.Combine(userPaths.Root, HostSettings.FileName);
        if (File.Exists(source)) File.Copy(source, target);
        else HostSettings.Load(servicePaths);   // schreibt die Vorgabe mit dem Modellordner dieses Benutzers
    }
}
