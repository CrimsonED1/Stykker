using StykkerStarter.Core.Config;

namespace StykkerStarter.Tests;

// Doppelte Einträge: gleiche Programmdatei (Groß/Kleinschreibung egal) und gleiche Argumente (getrimmt)
public class DuplicateTests
{
    private static StarterConfig Config(params ProgramEntry[] programs) => new() { Programs = programs.ToList() };

    private static ProgramEntry Entry(string id, string exe, string args = "") =>
        new() { Id = id, Name = id.ToUpperInvariant(), SourceExe = exe, Arguments = args };

    [Fact]
    public void The_same_file_and_the_same_arguments_is_a_duplicate_whatever_the_case()
    {
        var config = Config(Entry("demo", @"C:\Programs\Demo\demo.exe", "--port 8080"));

        var found = ProgramCheck.FindDuplicate(config, @"c:\programs\demo\DEMO.exe", "  --port 8080  ");

        Assert.Equal("demo", found?.Id);
    }

    [Fact]
    public void Other_arguments_or_another_file_are_not_a_duplicate()
    {
        var config = Config(Entry("demo", @"C:\Programs\Demo\demo.exe", "--port 8080"));

        Assert.Null(ProgramCheck.FindDuplicate(config, @"C:\Programs\Demo\demo.exe", "--port 9090"));
        Assert.Null(ProgramCheck.FindDuplicate(config, @"C:\Programs\Demo\demo.exe", ""));
        Assert.Null(ProgramCheck.FindDuplicate(config, @"C:\Programs\Other\demo.exe", "--port 8080"));
    }

    [Fact]
    public void An_entry_can_be_excluded_when_it_is_the_one_being_edited()
    {
        var config = Config(Entry("demo", @"C:\Programs\Demo\demo.exe"));

        Assert.Null(ProgramCheck.FindDuplicate(config, @"C:\Programs\Demo\demo.exe", "", exceptId: "demo"));
    }

    [Fact]
    public void A_relative_path_names_the_same_file_as_the_full_path()
    {
        var full = Path.GetFullPath("demo.exe");
        var config = Config(Entry("demo", full));

        Assert.Equal("demo", ProgramCheck.FindDuplicate(config, "demo.exe", null)?.Id);
    }

    [Fact]
    public void An_entry_with_an_empty_or_invalid_path_matches_nothing()
    {
        var config = Config(Entry("broken", ""), Entry("bad", "C:\\bad\0name.exe"));

        Assert.Null(ProgramCheck.FindDuplicate(config, "", ""));
        Assert.Null(ProgramCheck.FindDuplicate(config, "C:\\bad\0name.exe", ""));
    }
}
