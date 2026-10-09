using System.Diagnostics;
using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Elevation;

namespace StykkerCmd.Platform.Linux;

// Startet den Helfer über pkexec. Der Polkit-Dialog fragt nach dem Passwort; Abbruch ergibt Exit-Code 126.
[SupportedOSPlatform("linux")]
public sealed class LinuxElevation : IElevation
{
    private const int PkexecDismissed = 126;
    private const int PkexecNotFound = 127;

    public async Task<ElevationResult> RunHelperAsync(string encodedPayload, CancellationToken ct)
    {
        var (fileName, arguments) = ElevatedHelper.CommandFor(encodedPayload);

        var start = new ProcessStartInfo("pkexec")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(fileName);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("pkexec startet nicht.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ElevationResult(ElevationOutcome.Unsupported, PkexecNotFound);
        }

        using (process)
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode switch
            {
                PkexecDismissed => new ElevationResult(ElevationOutcome.Denied, process.ExitCode),
                PkexecNotFound => new ElevationResult(ElevationOutcome.Unsupported, process.ExitCode),
                _ => new ElevationResult(ElevationOutcome.Completed, process.ExitCode),
            };
        }
    }
}
