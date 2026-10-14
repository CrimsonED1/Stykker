using StykkerStarter.Core;

namespace StykkerStarter.Tests;

public class DataLayoutTests
{
    [Fact]
    public void VersionDir_is_program_id_then_version()
    {
        var layout = new DataLayout(Path.Combine(Path.GetTempPath(), "stykker-starter-test"));
        var dir = layout.VersionDir("stykker-llm", "0.5.2");

        Assert.Equal(Path.Combine(layout.Root, "stykker-llm", "0.5.2"), dir);
        Assert.Equal(Path.Combine(layout.Root, DataLayout.ConfigFileName), layout.ConfigFile);
    }

    [Fact]
    public void Version_characters_Windows_rejects_become_underscores()
    {
        Assert.Equal("1.0_2", DataLayout.SafeFolderName("1.0:2"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Program_id_must_be_a_plain_folder_name(string id)
    {
        var layout = new DataLayout(Path.Combine(Path.GetTempPath(), "stykker-starter-test"));
        Assert.ThrowsAny<ArgumentException>(() => layout.ProgramDir(id));
    }
}
