using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

public sealed class ServiceTokensTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string ServicesFile => Path.Combine(_folder.Root, ServiceTokens.FileName);

    [Fact]
    public void Asking_twice_for_a_service_gives_the_same_token()
    {
        var tokens = new ServiceTokens(ServicesFile, DataProtectors.None);

        var first = tokens.GetOrCreate("qwen");

        Assert.Equal(first, tokens.GetOrCreate("qwen"));
        Assert.Equal(43, first.Length);   // 32 Zufallsbytes, Base64url ohne Auffüllung
        Assert.NotEqual(first, tokens.GetOrCreate("other"));
    }

    [Fact]
    public void Two_instances_on_the_same_file_see_the_same_token()
    {
        var token = new ServiceTokens(ServicesFile, DataProtectors.None).GetOrCreate("qwen");

        Assert.Equal(token, new ServiceTokens(ServicesFile, DataProtectors.None).GetOrCreate("qwen"));
    }

    [Fact]
    public void With_a_protector_the_token_is_not_in_the_file_in_clear()
    {
        var token = new ServiceTokens(ServicesFile, new XorProtector(0x5A)).GetOrCreate("qwen");

        var raw = File.ReadAllText(ServicesFile);
        Assert.DoesNotContain(token, raw);
        Assert.False(raw.TrimStart().StartsWith('{'));
        Assert.Equal(token, new ServiceTokens(ServicesFile, new XorProtector(0x5A)).GetOrCreate("qwen"));
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_and_replaced()
    {
        File.WriteAllText(ServicesFile, "this is neither json nor base64 !!");

        var token = new ServiceTokens(ServicesFile, DataProtectors.None).GetOrCreate("qwen");

        Assert.Equal(43, token.Length);
        var kept = Assert.Single(Directory.GetFiles(_folder.Root, ServiceTokens.FileName + ".unreadable-*"));
        Assert.Equal("this is neither json nor base64 !!", File.ReadAllText(kept));
        Assert.Equal(token, new ServiceTokens(ServicesFile, DataProtectors.None).GetOrCreate("qwen"));
    }

    [Fact]
    public void A_file_written_under_another_protector_is_unreadable_and_kept_aside()
    {
        var old = new ServiceTokens(ServicesFile, new XorProtector(0x5A)).GetOrCreate("qwen");

        var fresh = new ServiceTokens(ServicesFile, new XorProtector(0x33)).GetOrCreate("qwen");

        Assert.NotEqual(old, fresh);
        Assert.Single(Directory.GetFiles(_folder.Root, ServiceTokens.FileName + ".unreadable-*"));
    }

    [Fact]
    public void Writing_leaves_no_temp_file_behind()
    {
        var tokens = new ServiceTokens(ServicesFile, DataProtectors.None);
        foreach (var id in new[] { "a", "b", "c" }) tokens.GetOrCreate(id);

        Assert.Equal([ServiceTokens.FileName], Directory.GetFiles(_folder.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void The_file_sits_next_to_the_pairing_file()
    {
        var pairing = Path.Combine(_folder.Root, PairingStore.FileName);

        Assert.Equal(ServicesFile, ServiceTokens.NextTo(pairing));
    }

    [Fact]
    public void A_service_id_with_other_characters_is_refused()
    {
        var tokens = new ServiceTokens(ServicesFile, DataProtectors.None);

        Assert.Throws<ArgumentException>(() => tokens.GetOrCreate("../x"));
        Assert.Throws<ArgumentException>(() => tokens.GetOrCreate(""));
    }

    [Fact]
    public void Dpapi_binds_the_tokens_to_the_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = new ServiceTokens(ServicesFile).GetOrCreate("qwen");

        var raw = File.ReadAllText(ServicesFile);
        Assert.DoesNotContain(token, raw);
        Assert.Equal(token, new ServiceTokens(ServicesFile).GetOrCreate("qwen"));
        // Ein anderer Zusatzwert liest sie nicht: die Ablage wird beiseitegelegt und ein neues Token entsteht
        Assert.NotEqual(token, new ServiceTokens(ServicesFile, DataProtectors.ForCurrentUser("something-else")).GetOrCreate("qwen"));
    }

    // Ein Schutz für die Tests, der überall läuft: XOR mit einem Schlüsselbyte. Er ersetzt DPAPI nur in der Prüfung,
    // ob der Klartext in der Datei steht und ob eine Datei unter einem anderen Schutz unlesbar ist.
    private sealed class XorProtector(byte key) : IDataProtector
    {
        public byte[]? Protect(byte[] data) => data.Select(b => (byte)(b ^ key)).ToArray();
        public byte[]? Unprotect(byte[] data) => Protect(data);
    }
}
