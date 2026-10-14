using Stykker.Shared.Config;

namespace Stykker.Shared.Tests;

// stykker.json wird beim nächsten Zugriff neu gelesen, wenn sie sich geändert hat (Ziel und Token wirken ohne Neustart).
public class HubConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stykker-hubconfig-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "stykker.json");
    private static readonly string[] Own = ["192.0.2.10"];

    public HubConfigFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private HubConfigFile Config() => new(File_, () => Own);

    private static string Entry(string target, string token) => $$"""
        { "modules": [ { "id": "qwen", "name": "Qwen Code", "external": true, "port": 17450,
                         "target": "{{target}}", "token": "{{token}}" } ] }
        """;

    [Fact]
    public void A_changed_target_and_token_are_read_without_a_restart()
    {
        File.WriteAllText(File_, Entry("http://127.0.0.1:16000", "first-token"));
        var config = Config();
        var before = Assert.Single(config.Current());
        Assert.Equal("http://127.0.0.1:16000", before.Target);

        File.WriteAllText(File_, Entry("http://192.0.2.10:16001", "second-token-longer"));
        File.SetLastWriteTimeUtc(File_, DateTime.UtcNow.AddMinutes(1));
        var after = Assert.Single(config.Current());

        Assert.Equal("http://192.0.2.10:16001", after.Target);
        Assert.Equal("second-token-longer", after.Token);
    }

    [Fact]
    public void An_unchanged_file_is_not_read_again()
    {
        File.WriteAllText(File_, Entry("http://127.0.0.1:16000", "first-token"));
        var config = Config();

        Assert.Same(config.Current(), config.Current());
    }

    [Fact]
    public void A_file_that_cannot_be_read_keeps_the_last_good_state()
    {
        File.WriteAllText(File_, Entry("http://127.0.0.1:16000", "first-token"));
        var config = Config();
        var good = config.Current();

        File.WriteAllText(File_, "{ not json");
        File.SetLastWriteTimeUtc(File_, DateTime.UtcNow.AddMinutes(1));

        Assert.Same(good, config.Current());
    }

    [Fact]
    public void A_missing_file_gives_the_defaults()
    {
        Assert.Same(HubModules.Defaults, Config().Current());
    }

    [Fact]
    public void The_file_path_is_made_absolute()
    {
        Assert.True(Path.IsPathRooted(new HubConfigFile("stykker.json", () => []).FilePath));
    }
}
