using System.Diagnostics;

namespace StykkerFirewall.Backend.WindowsFirewall;

// Überwachungsrichtlinie „Filterplattformverbindung“ (Fehler) schreibt Ereignis 5157 bei jeder blockierten
// Verbindung. Angesprochen über die GUID der Unterkategorie, nicht über den Namen – der ist übersetzt.
public static class AuditPolicy
{
    public const string FilteringPlatformConnection = "{0CCE9226-69AE-11D9-BED3-505054503030}";

    private const int SettingFailure = 2;   // Setting Value: 0 keine, 1 Erfolg, 2 Fehler, 3 beides

    public static void EnableFailureAuditing()
    {
        // Ändert nur „Fehler“; eine schon eingeschaltete Erfolgsüberwachung bleibt, wie sie ist.
        Run($"/set /subcategory:{FilteringPlatformConnection} /failure:enable");
    }

    public static bool IsFailureAuditingEnabled()
    {
        var file = Path.Combine(Path.GetTempPath(), $"stykker-fw-audit-{Guid.NewGuid():N}.csv");
        try
        {
            Run($"/backup /file:\"{file}\"");
            return ParseBackup(File.ReadAllLines(file), FilteringPlatformConnection) is { } v && (v & SettingFailure) != 0;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    // CSV aus „auditpol /backup“: Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,
    // Exclusion Setting,Setting Value. Die Spaltenköpfe sind englisch, die Texte in den Zeilen übersetzt;
    // gelesen wird nur die GUID und der Zahlenwert in der letzten Spalte.
    public static int? ParseBackup(IEnumerable<string> lines, string subcategoryGuid)
    {
        foreach (var line in lines)
        {
            if (!line.Contains(subcategoryGuid, StringComparison.OrdinalIgnoreCase)) continue;
            var cells = line.Split(',');
            if (cells.Length < 7) continue;
            if (int.TryParse(cells[^1].Trim().Trim('"'), out var value)) return value;
        }
        return null;
    }

    private static void Run(string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "auditpol.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("auditpol.exe did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("auditpol.exe did not finish"); }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"auditpol.exe failed ({p.ExitCode}): {(stderr.Result + stdout.Result).Trim()}");
    }
}
