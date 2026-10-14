using System.Security.Cryptography;
using System.Text;
using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

// Das Token eines fremden Dienstes aus einer Datei: als Text ("none") oder als DPAPI-Blob ("dpapi-user:<Zusatzwert>").
public sealed class ServiceTokenFileTests : IDisposable
{
    // Der Zusatzwert, den die Desktop-App des Qwen-Forks für ihr Token nimmt (daemon_connection.rs)
    private const string AppEntropy = "qwen-desktop.daemon-token.v1";
    private const string AppProtection = "dpapi-user:" + AppEntropy;

    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string TokenFile => Path.Combine(_folder.Root, "daemon-token.bin");

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // Ein Schutz für die Tests, der überall läuft: XOR mit einem Schlüsselbyte. Er zählt die Entschlüsselungen.
    private sealed class CountingXor(byte key) : IDataProtector
    {
        public int Unprotects { get; private set; }

        public byte[]? Protect(byte[] data) => Xor(data);

        public byte[]? Unprotect(byte[] data)
        {
            Unprotects++;
            return Xor(data);
        }

        private byte[] Xor(byte[] data) => data.Select(b => (byte)(b ^ key)).ToArray();
    }

    [Fact]
    public void A_text_token_with_no_protection_is_read_and_trailing_space_is_dropped()
    {
        var token = NewToken();
        File.WriteAllText(TokenFile, token + "\r\n");

        var read = ServiceTokenFile.Read(TokenFile, "none");

        Assert.Null(read.Problem);
        Assert.Equal(token, read.Token);
    }

    [Fact]
    public void A_byte_order_mark_in_front_of_a_text_token_is_not_part_of_it()
    {
        var token = NewToken();
        File.WriteAllText(TokenFile, "﻿" + token, new UTF8Encoding(false));

        Assert.Equal(token, ServiceTokenFile.Read(TokenFile, "none").Token);
    }

    [Fact]
    public void A_protected_token_is_opened_with_the_entropy_the_file_names()
    {
        var token = NewToken();
        var protector = new CountingXor(0x5A);
        File.WriteAllBytes(TokenFile, protector.Protect(Encoding.UTF8.GetBytes(token))!);
        string? entropy = null;

        var read = ServiceTokenFile.Read(TokenFile, "dpapi-user:" + AppEntropy, name => { entropy = name; return protector; });

        Assert.Null(read.Problem);
        Assert.Equal(token, read.Token);
        Assert.Equal(AppEntropy, entropy);
    }

    [Fact]
    public void A_blob_written_by_the_real_dpapi_is_read_and_another_entropy_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = NewToken();
        var blob = new WindowsDpapi(AppEntropy).Protect(Encoding.UTF8.GetBytes(token));
        Assert.NotNull(blob);
        File.WriteAllBytes(TokenFile, blob);

        var read = ServiceTokenFile.Read(TokenFile, AppProtection);

        Assert.Null(read.Problem);
        Assert.Equal(token, read.Token);
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(File.ReadAllBytes(TokenFile)));

        var refused = ServiceTokenFile.Read(TokenFile, "dpapi-user:some-other-app.v1");
        Assert.Null(refused.Token);
        Assert.Contains("cannot be unprotected", refused.Problem);
    }

    [Fact]
    public void Random_bytes_under_the_real_dpapi_are_refused_without_a_crash()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllBytes(TokenFile, RandomNumberGenerator.GetBytes(96));

        var read = ServiceTokenFile.Read(TokenFile, AppProtection);

        Assert.Null(read.Token);
        Assert.NotNull(read.Problem);
    }

    [Theory]
    [InlineData("dpapi-machine:qwen-desktop.daemon-token.v1")]
    [InlineData("dpapi-user:")]
    [InlineData("plain")]
    [InlineData("NONE")]
    [InlineData("")]
    public void An_unknown_protection_is_refused(string protection)
    {
        File.WriteAllText(TokenFile, NewToken());

        var read = ServiceTokenFile.Read(TokenFile, protection);

        Assert.Null(read.Token);
        Assert.Contains("tokenProtection", read.Problem);
        Assert.False(ServiceTokenFile.ProtectionKnown(protection));
    }

    [Fact]
    public void A_missing_file_is_refused()
    {
        var read = ServiceTokenFile.Read(TokenFile, "none");

        Assert.Null(read.Token);
        Assert.Contains("missing", read.Problem);
    }

    [Fact]
    public void A_file_that_is_too_large_is_refused()
    {
        File.WriteAllText(TokenFile, new string('a', ServiceTokenFile.MaxFileBytes + 1));

        var read = ServiceTokenFile.Read(TokenFile, "none");

        Assert.Null(read.Token);
        Assert.Contains("too large", read.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    public void A_token_that_cannot_go_into_a_header_is_refused(string content)
    {
        File.WriteAllText(TokenFile, content);

        var read = ServiceTokenFile.Read(TokenFile, "none");

        Assert.Null(read.Token);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void A_reason_never_contains_the_token_or_the_content()
    {
        var token = NewToken();
        File.WriteAllText(TokenFile, token + " with a space");

        var read = ServiceTokenFile.Read(TokenFile, "none");

        Assert.NotNull(read.Problem);
        Assert.DoesNotContain(token, read.Problem);
    }

    [Fact]
    public void The_file_is_read_again_only_when_its_time_or_length_changes()
    {
        var first = NewToken();
        var second = NewToken();
        var protector = new CountingXor(0x5A);
        File.WriteAllBytes(TokenFile, protector.Protect(Encoding.UTF8.GetBytes(first))!);
        var file = new ServiceTokenFile(TokenFile, AppProtection, _ => protector);

        Assert.Equal(first, file.Current().Token);
        Assert.Equal(first, file.Current().Token);
        Assert.Equal(1, protector.Unprotects);   // zweimal gefragt, einmal gelesen

        // Neuer Inhalt mit neuer Änderungszeit: wird neu gelesen
        File.WriteAllBytes(TokenFile, protector.Protect(Encoding.UTF8.GetBytes(second))!);
        File.SetLastWriteTimeUtc(TokenFile, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(second, file.Current().Token);
        Assert.Equal(2, protector.Unprotects);
    }

    [Fact]
    public void A_file_that_disappears_is_reported_missing()
    {
        File.WriteAllText(TokenFile, NewToken());
        var file = new ServiceTokenFile(TokenFile, "none");
        Assert.NotNull(file.Current().Token);

        File.Delete(TokenFile);

        var read = file.Current();
        Assert.Null(read.Token);
        Assert.Contains("missing", read.Problem);
    }
}
