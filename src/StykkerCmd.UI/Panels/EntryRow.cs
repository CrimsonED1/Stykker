using StykkerCmd.Core.Model;
using StykkerCmd.UI.Mvvm;
using StykkerCmd.UI.Text;

namespace StykkerCmd.UI.Panels;

// Eine Zeile der Liste. Die Markierung gehört zur Zeile; das Dateisystem kennt sie nicht.
public sealed class EntryRow : ObservableObject
{
    private bool _marked;

    public EntryRow(FileEntry entry, bool isParent = false)
    {
        Entry = entry;
        IsParent = isParent;
    }

    public FileEntry Entry { get; }

    // Die Zeile ".." führt in den übergeordneten Ordner.
    public bool IsParent { get; }

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    public string Icon => IsParent ? "↑" : Entry.IsLink ? "↪" : Entry.IsDirectory ? "▸" : "·";

    public string SizeText => IsParent ? string.Empty : Entry.IsDirectory ? "<DIR>" : Format.Size(Entry.Size);

    public string DateText => IsParent ? string.Empty : Format.Date(Entry.Modified);

    public bool IsMarked
    {
        get => _marked;
        set => Set(ref _marked, value);
    }
}
