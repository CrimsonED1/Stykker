using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;

namespace StykkerStarter.Core.Versions;

// Liest die Dateiversion einer .exe. Die Umsetzung steckt im Platform-Projekt (FileVersionInfo); Core kennt nur diese
// Schnittstelle. Liefert null, wenn die Datei keine Version hat.
public interface IFileVersionReader
{
    string? Read(string exePath);
}

public enum PlanKind
{
    UpToDate,   // die laufende Kopie ist der Stand der Quelle
    Reuse,      // der Stand liegt schon auf der Platte (z. B. beim Zurückgehen): nichts zu kopieren
    Copy,       // neue Kopie nötig, in den Ordner Folder
}

public sealed record CopyPlan(PlanKind Kind, string Version, string Folder, string ListHash);

// Entscheidet, welche Version die Quelle ist und ob sie kopiert werden muss.
// Version: die Dateiversion der .exe. Fehlt sie, die ersten acht Zeichen des Listen-Hashes.
// Ordner: die Version. Gibt es die Version schon mit anderem Inhalt, heißt der Ordner „<version>+<8 Zeichen>“.
public static class VersionPlanner
{
    // Die Version des Standes: die Dateiversion, sonst die ersten acht Zeichen des Listen-Hashes
    public static string VersionOf(SourceTree source, string? fileVersion) =>
        string.IsNullOrWhiteSpace(fileVersion) ? source.ListHash[..8] : fileVersion.Trim();

    public static CopyPlan Plan(ProgramEntry program, SourceTree source, string? fileVersion, DataLayout layout)
    {
        var listHash = source.ListHash;
        var version = VersionOf(source, fileVersion);

        var current = program.Current;
        if (current != null && current.Version == version && current.ListHash == listHash && IsOnDisk(layout, program, current.Folder))
            return new CopyPlan(PlanKind.UpToDate, version, current.Folder, listHash);

        var same = program.Copies.FirstOrDefault(c => c.Version == version && c.ListHash == listHash);
        if (same != null && IsOnDisk(layout, program, same.Folder))
            return new CopyPlan(PlanKind.Reuse, version, same.Folder, listHash);

        var folder = DataLayout.SafeFolderName(version);
        if (IsTaken(layout, program, folder)) folder = $"{folder}+{listHash[..8]}";
        if (IsTaken(layout, program, folder))
            throw new InvalidOperationException($"the folder for version {version} is already taken");
        return new CopyPlan(PlanKind.Copy, version, folder, listHash);
    }

    private static bool IsOnDisk(DataLayout layout, ProgramEntry program, string folder) =>
        Directory.Exists(Path.Combine(layout.ProgramDir(program.Id), folder));

    private static bool IsTaken(DataLayout layout, ProgramEntry program, string folder) =>
        program.Copies.Any(c => c.Folder == folder) || Directory.Exists(Path.Combine(layout.ProgramDir(program.Id), folder))
        || Directory.Exists(Path.Combine(layout.ProgramDir(program.Id), folder + DataLayout.PartialSuffix));
}
