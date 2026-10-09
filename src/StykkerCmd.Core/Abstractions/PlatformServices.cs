namespace StykkerCmd.Core.Abstractions;

// Papierkorb des Betriebssystems. MoveToTrash wirft bei Fehlern.
public interface ITrash
{
    bool IsAvailable(string path);

    void MoveToTrash(string path);
}

public sealed record LockHolder(int ProcessId, string ProcessName);

// Prozesse, die eine Datei offen halten (Windows: Restart Manager, Linux: /proc).
public interface ILockInspector
{
    IReadOnlyList<LockHolder> FindHolders(string path);
}

public enum ElevationOutcome
{
    Completed,
    Denied,
    Unsupported,
}

public sealed record ElevationResult(ElevationOutcome Outcome, int ExitCode);

// Startet den Helfer mit erhöhten Rechten (UAC unter Windows, pkexec unter Linux). Übergeben wird nur die kodierte Schrittliste.
public interface IElevation
{
    Task<ElevationResult> RunHelperAsync(string encodedPayload, CancellationToken ct);
}

public interface IShell
{
    void Open(string path);
}

// Holt ein Fenster nach einem Dialog in den Vordergrund. Windows verweigert das gelegentlich, wenn ein anderes Programm aktiv ist.
public interface IWindowFocus
{
    void BringToFront(IntPtr windowHandle);
}

// Alles, was die Oberfläche von der Plattform braucht.
public interface IPlatformServices
{
    IFileSystem FileSystem { get; }

    ITrash Trash { get; }

    ILockInspector LockInspector { get; }

    IElevation Elevation { get; }

    IShell Shell { get; }

    IWindowFocus WindowFocus { get; }
}

public sealed record PlatformServices(
    IFileSystem FileSystem,
    ITrash Trash,
    ILockInspector LockInspector,
    IElevation Elevation,
    IShell Shell,
    IWindowFocus WindowFocus) : IPlatformServices;
