using StykkerCmd.Core.Model;

namespace StykkerCmd.Core.Listing;

// Sortiert Einträge. Ordner stehen immer vor Dateien; die Richtung gilt innerhalb dieser Gruppen.
public static class EntrySorter
{
    public static IReadOnlyList<FileEntry> Sort(IEnumerable<FileEntry> entries, SortField field, SortDirection direction)
    {
        IOrderedEnumerable<FileEntry> ordered = entries.OrderBy(e => e.IsDirectory ? 0 : 1);

        ordered = field switch
        {
            SortField.Name => Then(ordered, e => e.Name, NaturalComparer.Instance, direction),
            SortField.Extension => Then(ordered, e => e.Extension, NaturalComparer.Instance, direction),
            SortField.Size => Then(ordered, e => e.Size, Comparer<long>.Default, direction),
            _ => Then(ordered, e => e.Modified, Comparer<DateTimeOffset>.Default, direction),
        };

        // Gleiche Schlüssel bekommen eine feste Reihenfolge, damit die Liste beim Neuladen nicht springt.
        return ordered.ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static IOrderedEnumerable<FileEntry> Then<TKey>(
        IOrderedEnumerable<FileEntry> source,
        Func<FileEntry, TKey> key,
        IComparer<TKey> comparer,
        SortDirection direction)
        => direction == SortDirection.Ascending
            ? source.ThenBy(key, comparer)
            : source.ThenByDescending(key, comparer);
}
