using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Core.Tests.Support;

// Papierkorb-Attrappe: entfernt Einträge aus dem Speicher-Dateisystem und merkt sich, was dorthin ging.
public sealed class MemoryTrash(MemoryFileSystem fs) : ITrash
{
    public bool Available { get; set; } = true;

    public List<string> Trashed { get; } = [];

    public bool IsAvailable(string path) => Available;

    public void MoveToTrash(string path)
    {
        if (!Available)
            throw new IOException("Auf diesem Datenträger gibt es keinen Papierkorb.");

        fs.RemoveTree(path);
        Trashed.Add(path);
    }
}
