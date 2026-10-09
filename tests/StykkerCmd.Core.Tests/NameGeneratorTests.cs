using StykkerCmd.Core.Operations;
using StykkerCmd.Core.Tests.Support;
using Xunit;

namespace StykkerCmd.Core.Tests;

public class NameGeneratorTests
{
    [Fact]
    public void Free_name_inserts_number_before_extension()
    {
        var fs = new MemoryFileSystem();
        fs.AddFile("/vol1/a.txt", "1");

        var name = NameGenerator.FreeName(fs, "/vol1/a.txt", fs.Contains);

        Assert.Equal("/vol1/a (2).txt", name);
    }

    [Fact]
    public void Free_name_skips_taken_numbers()
    {
        var fs = new MemoryFileSystem();
        fs.AddFile("/vol1/a.txt", "1");
        fs.AddFile("/vol1/a (2).txt", "2");

        var name = NameGenerator.FreeName(fs, "/vol1/a.txt", fs.Contains);

        Assert.Equal("/vol1/a (3).txt", name);
    }

    [Fact]
    public void Dot_file_keeps_whole_name_as_stem()
    {
        var fs = new MemoryFileSystem();
        fs.AddFile("/vol1/.gitignore", "x");

        var name = NameGenerator.FreeName(fs, "/vol1/.gitignore", fs.Contains);

        Assert.Equal("/vol1/.gitignore (2)", name);
    }
}
