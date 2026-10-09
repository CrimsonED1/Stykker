using System.Text.RegularExpressions;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// The static files the two demo hosts share. Nothing here runs a browser; it checks the files on disk, so a change in
/// one place that is not carried over to the other shows up in CI.
/// </summary>
public class DemoAssetsTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Shared = Path.Combine(Root, "samples", "Stykker.NanoCut.Demo.Shared", "wwwroot");

    /// <summary>
    /// The adapters next to viewer.js are copies of the npm packages in js/ (a linked file of a Razor class library is
    /// served empty by a host that runs from its build output), so they must stay byte for byte the same.
    /// </summary>
    [Theory]
    [InlineData("nanocut-three")]
    [InlineData("nanocut-babylon")]
    public void AdapterCopiesMatchTheNpmPackages(string package)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Root, "js", package, "index.js"));
        byte[] copy = File.ReadAllBytes(Path.Combine(Shared, "js", package, "index.js"));
        Assert.True(source.AsSpan().SequenceEqual(copy),
            $"samples/Stykker.NanoCut.Demo.Shared/wwwroot/js/{package}/index.js differs from js/{package}/index.js; copy it over");
    }

    /// <summary>Both host pages carry the same import map, and every entry names a file the shared library serves.</summary>
    [Fact]
    public void BothHostsImportTheSameSharedFiles()
    {
        var wasm = ImportMap(File.ReadAllText(Path.Combine(Root, "samples", "Stykker.NanoCut.Demo", "wwwroot", "index.html")));
        var server = ImportMap(File.ReadAllText(Path.Combine(Root, "samples", "Stykker.NanoCut.Server", "Components", "App.razor"))
            .Replace("@@", "@"));
        Assert.Equal(wasm, server);
        Assert.Equal(4, wasm.Count);
        const string prefix = "./_content/Stykker.NanoCut.Demo.Shared/";
        foreach (var (name, target) in wasm)
        {
            Assert.StartsWith(prefix, target);
            Assert.True(File.Exists(Path.Combine(Shared, target[prefix.Length..])), $"{name} → {target} does not exist");
        }
    }

    private static SortedDictionary<string, string> ImportMap(string page)
    {
        var map = new SortedDictionary<string, string>();
        foreach (Match m in Regex.Matches(page, "\"([^\"]+)\":\\s*\"(\\./_content/[^\"]+)\""))
            map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Stykker.NanoCut.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root (Stykker.NanoCut.slnx) not found");
    }
}
