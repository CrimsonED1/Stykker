using System.Security.Cryptography;
using System.Text;
using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

// Die Dateien unter Fixtures/ haben die Form, in der StykkerLLM access.dat und hosts.dat geschrieben hat
// (AccessState mit WriteIndented, HostFile ohne). Die Hashes werden hier mit dem alten Verfahren eingesetzt,
// unabhängig von PairingCodes.Hash nachgebaut.
public sealed class LegacyLlmImportTests : IDisposable
{
    private const string AdminCookie = "cookie-of-the-admin-phone";
    private const string ViewerCookie = "cookie-of-the-viewer";
    private const string NoRoleCookie = "cookie-from-before-roles";
    private const string HubCookie = "cookie-of-an-old-hub";
    private const string HostToken = "token-of-the-gpu-box";

    private readonly TestFolder _folder = new();
    private readonly string _llmDir;

    public LegacyLlmImportTests()
    {
        _llmDir = Path.Combine(_folder.Root, "StykkerLLM");
        Directory.CreateDirectory(_llmDir);
    }

    public void Dispose() => _folder.Dispose();

    private static string OldHash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("StykkerLLM.device.v1|" + token)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string AccessJson() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "llm-access.json"))
        .Replace("FIXTURE-ADMIN-HASH", OldHash(AdminCookie))
        .Replace("FIXTURE-VIEWER-HASH", OldHash(ViewerCookie))
        .Replace("FIXTURE-NOROLE-HASH", OldHash(NoRoleCookie))
        .Replace("FIXTURE-HUB-HASH", OldHash(HubCookie));

    private static string HostsJson() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "llm-hosts.json"))
        .Replace("FIXTURE-HOST-HASH", OldHash(HostToken));

    private void WriteOldFiles(Func<string, string>? wrap = null)
    {
        wrap ??= json => json;
        File.WriteAllText(Path.Combine(_llmDir, LegacyLlmImport.AccessFile), wrap(AccessJson()));
        File.WriteAllText(Path.Combine(_llmDir, LegacyLlmImport.HostsFile), wrap(HostsJson()));
    }

    [Fact]
    public void A_device_from_the_old_file_signs_in_with_its_old_cookie()
    {
        WriteOldFiles();

        var result = LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        var service = _folder.Service();
        Assert.Equal(new LegacyLlmImport.Result(Devices: 4, Hosts: 1), result);
        Assert.Equal(PairingRoles.Admin, service.RoleOf(AdminCookie, "llm"));
        Assert.Equal(PairingRoles.Viewer, service.RoleOf(ViewerCookie, "llm"));
        Assert.Equal(PairingRoles.Admin, service.RoleOf(NoRoleCookie, "llm"));    // Dateien aus der Zeit vor den Rollen
        Assert.Equal(PairingRoles.Viewer, service.RoleOf(HubCookie, "llm"));      // ein alter Hub liest nur noch
        Assert.Equal(PairingRoles.Viewer, service.RoleOf(AdminCookie, "sys"));    // in einem neuen Modul: zusehen
    }

    [Fact]
    public void A_host_from_the_old_file_keeps_its_token()
    {
        WriteOldFiles();

        LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        var host = _folder.Service().FindHost(HostToken);
        Assert.Equal("gpu-box", host?.Name);
        Assert.Equal("0123456789ab", host?.Id);
    }

    [Fact]
    public void Names_ids_times_code_and_the_remote_switch_are_taken_over()
    {
        WriteOldFiles();

        LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        var state = _folder.Store().Read();
        var phone = state.Devices.Single(d => d.Id == "a1b2c3d4");
        Assert.Equal("Chrome on Android", phone.Name);
        Assert.Equal("192.168.0.23", phone.Address);
        Assert.Equal(new DateTime(2026, 10, 1, 6, 15, 0, DateTimeKind.Utc), phone.Created);
        Assert.True(state.RemoteEnabled);
        Assert.Equal("482913", state.Code);
        Assert.Equal(5, state.Devices.Count);   // vier Geräte und ein Host; der kaputte Eintrag bleibt draußen
    }

    [Fact]
    public void The_old_files_are_only_read()
    {
        WriteOldFiles();
        var before = File.ReadAllText(Path.Combine(_llmDir, LegacyLlmImport.AccessFile));

        LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        Assert.Equal(before, File.ReadAllText(Path.Combine(_llmDir, LegacyLlmImport.AccessFile)));
        Assert.Equal(2, Directory.GetFiles(_llmDir).Length);
    }

    [Fact]
    public void A_device_removed_centrally_does_not_come_back_from_the_old_file()
    {
        WriteOldFiles();
        LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);
        Assert.True(_folder.Service().RemoveDevice("a1b2c3d4"));

        var again = LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        Assert.False(again.Any);
        Assert.Null(_folder.Service().RoleOf(AdminCookie, "llm"));
    }

    [Fact]
    public void Devices_already_paired_centrally_stay_and_keep_their_code()
    {
        var service = _folder.Service();
        var code = service.Code;
        var token = service.Pair(code, "new phone", null, "sys")!.Value.Token;
        var current = service.Code;
        WriteOldFiles();

        LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        Assert.Equal(PairingRoles.Admin, _folder.Service().RoleOf(token, "sys"));
        Assert.Equal(current, _folder.Service().Code);
        Assert.Equal(5, _folder.Service().Devices.Count);
    }

    [Fact]
    public void Nothing_to_import_changes_nothing()
    {
        var result = LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        Assert.False(result.Any);
        Assert.False(File.Exists(_folder.File));
    }

    [Fact]
    public void A_damaged_old_file_is_skipped()
    {
        File.WriteAllText(Path.Combine(_llmDir, LegacyLlmImport.AccessFile), "{ not json");

        var result = LegacyLlmImport.ImportFromFolder(_folder.Store(), _llmDir, DataProtectors.None);

        Assert.False(result.Any);
    }

    // Wie auf einem echten PC: die alten Dateien sind mit DPAPI und dem Zusatzwert von StykkerLLM geschützt.
    [Fact]
    public void Dpapi_protected_old_files_are_read_with_the_old_entropy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = DataProtectors.ForCurrentUser(DataProtectors.LegacyLlmEntropy);
        WriteOldFiles(json => Convert.ToBase64String(old.Protect(Encoding.UTF8.GetBytes(json))!));

        var store = new PairingStore(_folder.File);
        var result = LegacyLlmImport.ImportFromFolder(store, _llmDir);

        Assert.Equal(new LegacyLlmImport.Result(4, 1), result);
        Assert.Equal(PairingRoles.Admin, new PairingService(store).RoleOf(AdminCookie, "llm"));
        Assert.DoesNotContain("Chrome on Android", File.ReadAllText(_folder.File));
    }
}
