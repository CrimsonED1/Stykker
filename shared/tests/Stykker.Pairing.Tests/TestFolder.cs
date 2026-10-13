using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

// Ein eigener Ordner je Test, mit einer Ablage ohne Benutzerbindung (Klartext): die Tests lesen die Datei direkt.
public sealed class TestFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "stykker-pairing-tests-" + Guid.NewGuid().ToString("N"));
    public string File => Path.Combine(Root, PairingStore.FileName);

    public TestFolder() => Directory.CreateDirectory(Root);

    public PairingStore Store() => new(File, DataProtectors.None);

    public PairingService Service(Func<DateTime>? now = null) => new(Store(), now);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* ein Virenscanner hält noch eine Datei: der Temp-Ordner wird später geleert */ }
    }
}

// Eine Uhr zum Vorstellen.
public sealed class Clock
{
    public DateTime Now { get; set; } = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    public DateTime Read() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
