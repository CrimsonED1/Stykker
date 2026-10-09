using System.Collections.ObjectModel;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Listing;
using StykkerCmd.Core.Model;
using StykkerCmd.UI.Mvvm;
using StykkerCmd.UI.Text;

namespace StykkerCmd.UI.Panels;

// Zustand eines Panels: Ordner, Liste, Sortierung, Filter, Markierungen und Cursor.
public sealed class PanelViewModel : ObservableObject
{
    private readonly IFileSystem _fs;
    private readonly HashSet<string> _marked = new(StringComparer.Ordinal);
    private IReadOnlyList<FileEntry> _listing = [];
    private ObservableCollection<EntryRow> _rows = [];
    private string _directory;
    private string _filter = string.Empty;
    private string? _error;
    private string _summary = string.Empty;
    private int _cursorIndex = -1;
    private int _loadVersion;
    private SortField _sortField = SortField.Name;
    private SortDirection _sortDirection = SortDirection.Ascending;
    private ObservableCollection<VolumeTab> _volumes = [];

    // Pro Datenträger der zuletzt geöffnete Ordner; ein Wechsel auf den Datenträger geht dorthin zurück.
    private readonly Dictionary<string, string> _lastFolderByRoot = new(StringComparer.OrdinalIgnoreCase);

    public PanelViewModel(IFileSystem fileSystem, string directory)
    {
        _fs = fileSystem;
        _directory = directory;
    }

    public string CurrentDirectory => _directory;

    public string DisplayPath => _directory;

    public bool IsRoot => _fs.ParentOf(_directory) == _directory;

    public ObservableCollection<EntryRow> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public ObservableCollection<VolumeTab> Volumes
    {
        get => _volumes;
        private set => Set(ref _volumes, value);
    }

    public int CursorIndex
    {
        get => _cursorIndex;
        set => Set(ref _cursorIndex, value);
    }

    public EntryRow? CursorRow => _cursorIndex >= 0 && _cursorIndex < _rows.Count ? _rows[_cursorIndex] : null;

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
                Rebuild(CursorRow?.Entry.Name);
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public SortField SortField => _sortField;

    public string SortLabel
        => $"{FieldName(_sortField)} {(_sortDirection == SortDirection.Ascending ? "↑" : "↓")}";

    // Lädt den Ordner neu. Veraltete Ergebnisse werden verworfen, falls inzwischen weitergeblättert wurde.
    public async Task LoadAsync(string? selectName = null)
    {
        var version = ++_loadVersion;
        var directory = _directory;
        try
        {
            var listing = await Task.Run(() => _fs.List(directory));
            if (version != _loadVersion)
                return;
            _listing = listing;
            _error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (version != _loadVersion)
                return;
            _listing = [];
            _error = ex is UnauthorizedAccessException ? "Zugriff verweigert." : ex.Message;
        }

        RebuildVolumes();
        Rebuild(selectName ?? CursorRow?.Entry.Name);
    }

    public Task NavigateAsync(string directory, string? selectName = null)
    {
        _directory = directory;
        _lastFolderByRoot[RootOf(directory)] = directory;
        _marked.Clear();
        _filter = string.Empty;
        Raise(nameof(CurrentDirectory));
        Raise(nameof(DisplayPath));
        Raise(nameof(IsRoot));
        Raise(nameof(Filter));
        return LoadAsync(selectName);
    }

    public Task GoUpAsync()
    {
        if (IsRoot)
            return Task.CompletedTask;

        var child = _fs.NameOf(_directory);
        return NavigateAsync(_fs.ParentOf(_directory), child);
    }

    // Enter: Ordner öffnen bzw. nach oben gehen. Für Dateien gibt die Methode den Eintrag zurück; die Ansicht öffnet ihn.
    public async Task<FileEntry?> ActivateCursorAsync()
    {
        var row = CursorRow;
        if (row is null)
            return null;

        if (row.IsParent)
        {
            await GoUpAsync();
            return null;
        }

        if (row.IsDirectory)
        {
            await NavigateAsync(row.Entry.FullPath);
            return null;
        }

        return row.Entry;
    }

    // Markiert den Eintrag unter dem Cursor und geht eine Zeile weiter.
    public void ToggleMarkAtCursor()
    {
        var row = CursorRow;
        if (row is null || row.IsParent)
            return;

        SetMarked(row, !row.IsMarked);
        if (_cursorIndex < _rows.Count - 1)
            CursorIndex = _cursorIndex + 1;
        UpdateSummary();
    }

    // Markiert alle sichtbaren Einträge; sind schon alle markiert, wird alles gelöst.
    public void MarkAll()
    {
        var files = _rows.Where(r => !r.IsParent).ToList();
        bool allMarked = files.Count > 0 && files.All(r => r.IsMarked);
        foreach (var row in files)
            SetMarked(row, !allMarked);
        UpdateSummary();
    }

    // Die Markierung zählt; ohne Markierung gilt der Eintrag unter dem Cursor.
    public IReadOnlyList<FileEntry> SelectedEntries()
    {
        var marked = _rows.Where(r => !r.IsParent && r.IsMarked).Select(r => r.Entry).ToList();
        if (marked.Count > 0)
            return marked;

        var cursor = CursorRow;
        return cursor is { IsParent: false }
            ? new List<FileEntry> { cursor.Entry }
            : new List<FileEntry>();
    }

    public void Sort(SortField field)
    {
        var keep = CursorRow?.Entry.Name;
        if (field == _sortField)
        {
            _sortDirection = _sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
        }
        else
        {
            _sortField = field;
            _sortDirection = SortDirection.Ascending;
        }

        Rebuild(keep);
        Raise(nameof(SortLabel));
        Raise(nameof(SortField));
    }

    public void ClearFilter()
    {
        if (_filter.Length > 0)
            Filter = string.Empty;
    }

    // Wechselt auf einen Datenträger und geht dort zu dem Ordner, der dort zuletzt offen war.
    public Task SwitchVolumeAsync(string root)
        => NavigateAsync(_lastFolderByRoot.TryGetValue(root, out var last) ? last : root);

    private void RebuildVolumes()
    {
        var current = RootOf(_directory);
        Volumes = new ObservableCollection<VolumeTab>(_fs.Volumes().Select(volume => new VolumeTab(
            volume.Root,
            LabelOf(volume),
            string.Equals(volume.Root, current, StringComparison.OrdinalIgnoreCase))));
    }

    // Kurze Datenträger wie "C:" zeigen den Buchstaben vor dem Namen; eingehängte Medien nur ihren Namen.
    private static string LabelOf(VolumeInfo volume)
    {
        var head = volume.Root.TrimEnd('\\', '/');
        if (head.Length == 0)
            head = "/";
        return head.Length <= 2 ? $"{head}  {volume.Name}" : volume.Name;
    }

    // Die Wurzel des Datenträgers: der Ordner, dessen Elternteil er selbst ist.
    private string RootOf(string path)
    {
        var current = path;
        for (var parent = _fs.ParentOf(current); parent != current; parent = _fs.ParentOf(current))
            current = parent;
        return current;
    }

    private void SetMarked(EntryRow row, bool marked)
    {
        row.IsMarked = marked;
        if (marked)
            _marked.Add(row.Entry.FullPath);
        else
            _marked.Remove(row.Entry.FullPath);
    }

    // Baut die Liste aus dem letzten Ergebnis neu: Filter, Sortierung, Markierungen, Cursor.
    private void Rebuild(string? keepName)
    {
        var rows = new ObservableCollection<EntryRow>();
        if (!IsRoot)
        {
            rows.Add(new EntryRow(
                new FileEntry("..", _fs.ParentOf(_directory), true, false, 0, DateTimeOffset.MinValue),
                isParent: true));
        }

        foreach (var entry in EntrySorter.Sort(EntryFilter.Apply(_listing, _filter), _sortField, _sortDirection))
            rows.Add(new EntryRow(entry) { IsMarked = _marked.Contains(entry.FullPath) });

        Rows = rows;
        // Immer melden, auch wenn sich der Index nicht ändert: die Liste hat ihre Auswahl beim Tausch der Zeilen zurückgesetzt.
        _cursorIndex = IndexOfName(keepName);
        Raise(nameof(CursorIndex));
        Raise(nameof(CursorRow));
        UpdateSummary();
    }

    private int IndexOfName(string? name)
    {
        if (_rows.Count == 0)
            return -1;
        if (name is null)
            return 0;

        for (int i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Entry.Name, name, StringComparison.Ordinal))
                return i;
        }
        return 0;
    }

    private void UpdateSummary()
    {
        int folders = 0, files = 0, markedCount = 0;
        long bytes = 0, markedBytes = 0;
        foreach (var row in _rows)
        {
            if (row.IsParent)
                continue;

            if (row.IsDirectory)
            {
                folders++;
            }
            else
            {
                files++;
                bytes += row.Entry.Size;
            }

            if (row.IsMarked)
            {
                markedCount++;
                if (!row.IsDirectory)
                    markedBytes += row.Entry.Size;
            }
        }

        var text = $"{Format.Count(folders, "Ordner", "Ordner")} · {Format.Count(files, "Datei", "Dateien")} · {Format.Size(bytes)}";
        if (markedCount > 0)
            text += $" · {markedCount} markiert ({Format.Size(markedBytes)})";
        if (_filter.Length > 0)
            text += $" · Filter: {_filter}";
        if (_error is not null)
            text = _error;

        Summary = text;
    }

    private static string FieldName(SortField field) => field switch
    {
        SortField.Name => "Name",
        SortField.Extension => "Erweiterung",
        SortField.Size => "Größe",
        _ => "Datum",
    };
}
