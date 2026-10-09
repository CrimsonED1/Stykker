using System.Diagnostics;
using StykkerCmd.Core.Listing;
using StykkerCmd.Core.Model;
using Xunit;

namespace StykkerCmd.Core.Tests;

public class ListingTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FileEntry File(string name, long size = 0, int day = 1)
        => new(name, "/x/" + name, false, false, size, Day1.AddDays(day - 1));

    private static FileEntry Dir(string name)
        => new(name, "/x/" + name, true, false, 0, Day1);

    [Fact]
    public void Natural_order_compares_digit_runs_as_numbers()
    {
        var sorted = new[] { "file10", "File2", "file1", "file20" }
            .OrderBy(n => n, NaturalComparer.Instance)
            .ToArray();

        Assert.Equal(new[] { "file1", "File2", "file10", "file20" }, sorted);
    }

    [Fact]
    public void Directories_come_before_files_in_every_sort_direction()
    {
        var entries = new[] { File("alpha.txt", 5), Dir("zeta"), File("beta.txt", 50) };

        var ascending = EntrySorter.Sort(entries, SortField.Name, SortDirection.Ascending);
        var descending = EntrySorter.Sort(entries, SortField.Name, SortDirection.Descending);

        Assert.Equal("zeta", ascending[0].Name);
        Assert.Equal("zeta", descending[0].Name);
    }

    [Fact]
    public void Size_descending_orders_files_largest_first_after_directories()
    {
        var entries = new[] { File("alpha.txt", 5), Dir("zeta"), File("beta.txt", 50) };

        var sorted = EntrySorter.Sort(entries, SortField.Size, SortDirection.Descending);

        Assert.Equal(new[] { "zeta", "beta.txt", "alpha.txt" }, sorted.Select(e => e.Name));
    }

    [Fact]
    public void Extension_sort_groups_by_extension_then_name()
    {
        var entries = new[] { File("b.txt"), File("a.md"), File("a.txt"), File("noext") };

        var sorted = EntrySorter.Sort(entries, SortField.Extension, SortDirection.Ascending);

        // Ohne Erweiterung kommt aufsteigend zuerst (leere Erweiterung ist die kleinste).
        Assert.Equal(new[] { "noext", "a.md", "a.txt", "b.txt" }, sorted.Select(e => e.Name));
    }

    [Theory]
    [InlineData("", "anything", true)]
    [InlineData("ABC", "xabcx", true)]
    [InlineData("*.txt", "a.txt", true)]
    [InlineData("*.txt", "a.txt.bak", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    public void Filter_matches_substrings_and_wildcards(string pattern, string name, bool expected)
        => Assert.Equal(expected, EntryFilter.CreateMatcher(pattern)(name));

    [Theory]
    [InlineData(".gitignore", "")]
    [InlineData("archiv.tar.gz", "gz")]
    [InlineData("Ordner.", "")]
    public void Extension_of_special_names(string name, string expected)
        => Assert.Equal(expected, FileEntry.ExtensionOf(name));

    [Fact]
    public void Fifty_thousand_entries_sort_and_filter_in_under_one_second()
    {
        var entries = new List<FileEntry>(50_000);
        for (int i = 0; i < 50_000; i++)
        {
            var name = $"eintrag-{(i * 7919) % 50_000:D6}.{(i % 3 == 0 ? "txt" : "bin")}";
            entries.Add(i % 10 == 0
                ? Dir($"ordner-{i:D5}")
                : File(name, size: (i * 31L) % 100_000, day: i % 28 + 1));
        }

        var clock = Stopwatch.StartNew();
        var sorted = EntrySorter.Sort(EntryFilter.Apply(entries, "eintrag"), SortField.Name, SortDirection.Ascending);
        clock.Stop();

        // 5 000 der 50 000 Einträge sind Ordner ("ordner-…") und fallen aus dem Filter "eintrag".
        Assert.Equal(45_000, sorted.Count);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"Sortieren dauerte {clock.ElapsedMilliseconds} ms.");
    }
}
