using System.ComponentModel;
using System.Diagnostics;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Elevation;

namespace StykkerCmd.Platform.Windows;

// Startet den Helfer über "Als Administrator ausführen". Lehnt der Nutzer die UAC-Abfrage ab, kommt Denied zurück.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsElevation : IElevation
{
    // ERROR_CANCELLED: die Abfrage wurde abgebrochen.
    private const int ErrorCancelled = 1223;

    public async Task<ElevationResult> RunHelperAsync(string encodedPayload, CancellationToken ct)
    {
        var (fileName, arguments) = ElevatedHelper.CommandFor(encodedPayload);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, JoinArguments(arguments))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Der Helfer konnte nicht gestartet werden.");

            await process.WaitForExitAsync(ct);
            return new ElevationResult(ElevationOutcome.Completed, process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevationResult(ElevationOutcome.Denied, -1);
        }
    }

    private static string JoinArguments(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
