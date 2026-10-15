using StykkerStarter.Core.Web;

namespace StykkerStarter.Tests;

public class PageJsonTests
{
    private static ProgramView Sample(string? broken = null) => new(
        Id: "demo", Name: "Demo", Source: @"C:\Programs\Demo\demo.exe", Arguments: "", Local: null, SourceVersion: "1.0",
        Reachable: true, UpdateVersion: null, Running: true, Pids: new[] { 42 }, Folder: null, HasCopy: false,
        StartWithStarter: true, UpdateWithoutAsking: false, KeepPrevious: true, CopyToLocal: false,
        ChangedSinceStart: false, Broken: broken);

    [Fact]
    public void The_page_gets_program_fields_in_camelCase()
    {
        var json = PageJson.Serialize(new { programs = new[] { Sample() } });

        Assert.Contains("\"name\":\"Demo\"", json);
        Assert.Contains("\"source\":", json);
        Assert.Contains("\"copyToLocal\":false", json);
        Assert.Contains("\"running\":true", json);
        Assert.Contains("\"reachable\":true", json);
        Assert.DoesNotContain("\"Name\"", json);
    }

    [Fact]
    public void A_broken_entry_gives_the_page_its_reason()
    {
        var json = PageJson.Serialize(new { programs = new[] { Sample("no source file") } });

        Assert.Contains("\"broken\":\"no source file\"", json);
    }
}
