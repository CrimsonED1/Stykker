using System.Reflection;
using System.Text.Json;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Operations;

namespace StykkerCmd.Core.Elevation;

// Der Helfer läuft mit erhöhten Rechten und führt genau die übergebenen Einzelschritte aus.
// Er kennt keine Shell, keine Befehlszeilen-Pfade und keine Muster: nur absolute Pfade, nur bekannte Schritte.
public static class ElevatedHelper
{
    // Startargument, mit dem die Anwendung als Helfer läuft (ohne Oberfläche).
    public const string Flag = "--elevated-run";

    public const int ExitOk = 0;
    public const int ExitInvalid = 1;
    public const int ExitPartial = 2;

    // Begrenzt die Befehlszeile (Windows: rund 32 000 Zeichen).
    public const int MaxOperations = 60;

    public static string Encode(IReadOnlyList<LeafOperation> operations)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new ElevatedPayload(operations.ToList()));
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static IReadOnlyList<LeafOperation> Decode(string encoded)
    {
        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        var payload = JsonSerializer.Deserialize<ElevatedPayload>(Convert.FromBase64String(base64))
            ?? throw new InvalidDataException("Leere Nutzlast.");
        return payload.Operations;
    }

    // Gibt null zurück, wenn die Schritte zulässig sind, sonst den Grund.
    public static string? Validate(IReadOnlyList<LeafOperation> operations)
    {
        if (operations.Count == 0 || operations.Count > MaxOperations)
            return "Die Anzahl der Schritte ist ungültig.";

        foreach (var op in operations)
        {
            if (!IsSafeAbsolute(op.Source) || (op.Target is not null && !IsSafeAbsolute(op.Target)))
                return "Nur absolute Pfade ohne '..' sind erlaubt.";

            bool needsTarget = op.Kind is LeafKind.CopyFile or LeafKind.MoveFile or LeafKind.CreateDirectory;
            if (needsTarget && op.Target is null)
                return "Ein Schritt braucht ein Ziel.";
        }

        return null;
    }

    // Führt die Schritte aus und liefert den Exit-Code für den aufrufenden Prozess.
    public static int Run(string encoded, IPlatformServices services)
    {
        IReadOnlyList<LeafOperation> operations;
        try
        {
            operations = Decode(encoded);
        }
        catch (Exception)
        {
            return ExitInvalid;
        }

        if (Validate(operations) is not null)
            return ExitInvalid;

        var runner = new OperationRunner(services.FileSystem, services.Trash);
        var result = runner.RunLeavesAsync(operations, progress: null, CancellationToken.None).GetAwaiter().GetResult();
        return result.Issues.Count == 0 ? ExitOk : ExitPartial;
    }

    // Programm und Argumente, mit denen der Helfer startet. Läuft die App als "dotnet StykkerCMD.dll", bleibt die DLL erhalten.
    public static (string FileName, IReadOnlyList<string> Arguments) CommandFor(string encodedPayload)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Der Programmpfad ist unbekannt.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Die Anwendungs-DLL ist unbekannt."));
        }

        arguments.Add(Flag);
        arguments.Add(encodedPayload);
        return (host, arguments);
    }

    private static bool IsSafeAbsolute(string path)
        => Path.IsPathFullyQualified(path)
           && !path.Split('\\', '/').Any(segment => segment == "..");

    public sealed record ElevatedPayload(List<LeafOperation> Operations);
}
