using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class DesignSystemHostTests
{
    [Fact]
    public void TheFlagWinsOverTheEnvironmentAndTheDefault()
    {
        var variable = NewVariable();
        Environment.SetEnvironmentVariable(variable, @"D:\from-env");
        try
        {
            Assert.Equal(@"D:\from-flag", DesignSystemHost.Resolve(new[] { "--design-system", @"D:\from-flag" }, variable));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void TheEnvironmentIsUsedWithoutAFlag()
    {
        var variable = NewVariable();
        Environment.SetEnvironmentVariable(variable, @"D:\from-env");
        try { Assert.Equal(@"D:\from-env", DesignSystemHost.Resolve(Array.Empty<string>(), variable)); }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void TheMonorepoFolderIsTheDefault() =>
        Assert.Equal(DesignSystemHost.DefaultFolder, DesignSystemHost.Resolve(Array.Empty<string>(), NewVariable()));

    [Fact]
    public void AFlagWithoutAValueFallsBack() =>
        Assert.Equal(DesignSystemHost.DefaultFolder, DesignSystemHost.Resolve(new[] { "--design-system" }, NewVariable()));

    [Fact]
    public void TheDefaultIsFoundAboveTheProgramFolder_WhereverTheRepoLives()
    {
        var root = Path.Combine(Path.GetTempPath(), "stykker-ds-" + Guid.NewGuid().ToString("N"));
        var design = Path.Combine(root, "shared", "design-system");
        var program = Path.Combine(root, "hud", "src", "Server", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(design);
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(design, "tokens.css"), ":root{}");
        try
        {
            Assert.Equal(design, DesignSystemHost.FindFrom(program));
            Assert.Null(DesignSystemHost.FindFrom(Path.GetPathRoot(root)!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Im Veröffentlichungs-Paket liegt design-system neben der exe: der Ordner gewinnt gegen eine Monorepo darüber.
    [Fact]
    public void ADesignSystemBesideTheProgramWinsOverTheRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "stykker-ds-" + Guid.NewGuid().ToString("N"));
        var program = Path.Combine(root, "publish");
        var beside = Path.Combine(program, "design-system");
        var repo = Path.Combine(root, "shared", "design-system");
        Directory.CreateDirectory(beside);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(beside, "tokens.css"), ":root{}");
        File.WriteAllText(Path.Combine(repo, "tokens.css"), ":root{}");
        try { Assert.Equal(beside, DesignSystemHost.FindFrom(program)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Die Tests liegen in der Monorepo: von hier aus muss die Vorgabe das echte Design-System treffen.
    [Fact]
    public void TheDefaultPointsAtTheDesignSystemOfThisRepo() =>
        Assert.True(File.Exists(Path.Combine(DesignSystemHost.DefaultFolder, "tokens.css")), DesignSystemHost.DefaultFolder);

    private static string NewVariable() => "STYKKER_TEST_" + Guid.NewGuid().ToString("N");
}
