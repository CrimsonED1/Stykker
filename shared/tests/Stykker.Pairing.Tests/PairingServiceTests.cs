using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

public sealed class PairingServiceTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly Clock _clock = new();

    public void Dispose() => _folder.Dispose();

    private PairingService Service() => _folder.Service(_clock.Read);

    [Fact]
    public void Hash_keeps_the_format_devices_were_stored_with()
    {
        // Ändert sich dieser Wert, ist jedes angemeldete Gerät abgemeldet und jeder Host getrennt.
        Assert.Equal("ITVjIDoi4GkTJfgNotj0r5G9c9jSLCpLeHYoCwenN9M", PairingCodes.Hash("known-token"));
    }

    [Fact]
    public void First_use_creates_a_six_digit_code()
    {
        var code = Service().Code;

        Assert.True(PairingCodes.IsCode(code));
        Assert.Equal(code, Service().Code);
    }

    [Fact]
    public void Pair_with_the_code_makes_the_device_admin_in_that_module_and_burns_the_code()
    {
        var service = Service();
        var code = service.Code;

        var paired = service.Pair(PairingCodes.Pretty(code), "Chrome on Android", "192.168.0.23", "llm");

        Assert.NotNull(paired);
        Assert.Equal(PairingRoles.Admin, service.RoleOf(paired.Value.Token, "llm"));
        Assert.NotEqual(code, service.Code);
        Assert.Null(service.Pair(code, "second", null, "llm"));
    }

    [Fact]
    public void A_new_module_sees_the_paired_device_as_viewer_without_pairing_again()
    {
        var service = Service();
        var token = service.Pair(service.Code, "phone", null, "llm")!.Value.Token;

        // Ein anderes Modul, mit eigener Ablage-Instanz wie in einem anderen Programm
        var other = Service();

        Assert.Equal(PairingRoles.Viewer, other.RoleOf(token, "sys"));
        Assert.False(PairingRoles.CanWrite(other.RoleOf(token, "sys")));
        Assert.Single(other.Devices);
    }

    [Fact]
    public void Roles_are_set_per_module()
    {
        var service = Service();
        var (token, device) = service.Pair(service.Code, "phone", null, "llm")!.Value;

        Assert.True(service.SetRole(device.Id, "sys", PairingRoles.Admin));
        Assert.True(service.SetRole(device.Id, "llm", PairingRoles.Viewer));

        Assert.Equal(PairingRoles.Admin, service.RoleOf(token, "sys"));
        Assert.Equal(PairingRoles.Viewer, service.RoleOf(token, "llm"));
        Assert.False(service.SetRole(device.Id, "sys", "owner"));
        Assert.False(service.SetRole("missing", "sys", PairingRoles.Admin));
    }

    [Fact]
    public void A_wrong_or_unknown_token_has_no_role()
    {
        var service = Service();
        service.Pair(service.Code, "phone", null, "llm");

        Assert.Null(service.RoleOf("not-a-token", "llm"));
        Assert.Null(service.RoleOf("", "llm"));
        Assert.Null(service.RoleOf(null, "llm"));
    }

    [Fact]
    public void Five_wrong_tries_replace_the_code_even_across_service_instances()
    {
        var code = Service().Code;
        var wrong = code == "000000" ? "111111" : "000000";

        // Jeder Versuch aus einem anderen "Prozess": gezählt wird in der Ablage
        for (int i = 0; i < PairingCodes.MaxWrongTries; i++)
            Assert.Null(Service().Pair(wrong, "guess", null, "llm"));

        Assert.NotEqual(code, Service().Code);
        Assert.Null(Service().Pair(code, "late", null, "llm"));
    }

    [Fact]
    public void An_expired_code_is_refused_even_when_it_is_the_right_one()
    {
        var service = Service();
        var code = service.Code;
        _clock.Advance(PairingCodes.CodeLifetime);

        Assert.Null(service.Pair(code, "late", null, "llm"));
        Assert.Empty(service.Devices);
    }

    [Fact]
    public void Maintain_rotates_an_expired_code_and_is_idle_otherwise()
    {
        var service = Service();
        var code = service.Code;

        Assert.False(service.Maintain());
        _clock.Advance(PairingCodes.CodeLifetime);
        Assert.True(service.Maintain());
        Assert.NotEqual(code, service.Code);
        Assert.False(service.Maintain());
    }

    [Fact]
    public void Removing_a_device_signs_out_exactly_that_device()
    {
        var service = Service();
        var first = service.Pair(service.Code, "phone", null, "llm")!.Value;
        var second = service.Pair(service.Code, "tablet", null, "llm")!.Value;

        Assert.True(service.RemoveDevice(first.Device.Id));

        Assert.Null(service.RoleOf(first.Token, "llm"));
        Assert.NotNull(service.RoleOf(second.Token, "llm"));
        Assert.True(service.RemoveToken(second.Token));
        Assert.Empty(service.Devices);
    }

    [Fact]
    public void Last_seen_is_written_at_most_once_a_minute()
    {
        var service = Service();
        var (token, device) = service.Pair(service.Code, "phone", "10.0.0.1", "llm")!.Value;
        var written = new FileInfo(_folder.File).LastWriteTimeUtc;

        service.RoleOf(token, "llm", "10.0.0.2");
        Assert.Equal(written, new FileInfo(_folder.File).LastWriteTimeUtc);

        _clock.Advance(TimeSpan.FromMinutes(2));
        service.RoleOf(token, "llm", "10.0.0.2");

        var stored = service.Devices.Single(d => d.Id == device.Id);
        Assert.Equal(_clock.Now, stored.LastSeen);
        Assert.Equal("10.0.0.2", stored.Address);
    }

    [Fact]
    public void The_file_holds_hashes_never_tokens()
    {
        var service = Service();
        var token = service.Pair(service.Code, "phone", null, "llm")!.Value.Token;
        var host = service.AddHost("gpu-box");
        var request = service.RequestPairing("tv", null, "llm")!.Value;

        var text = File.ReadAllText(_folder.File);

        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(host.Token, text);
        Assert.DoesNotContain(request.Secret, text);
        Assert.Contains(PairingCodes.Hash(token), text);
    }

    // ── Kopplung andersherum ──

    [Fact]
    public void Reverse_pairing_can_be_approved_by_another_process()
    {
        var module = Service();
        var (request, secret) = module.RequestPairing("Living room TV", "192.168.0.40", "llm")!.Value;
        Assert.Equal((PairStatus.Pending, null), module.Poll(request.Id, secret));

        // Die Zentrale: eine andere Instanz, die nur die Ablage teilt
        var central = Service();
        Assert.Single(central.PendingRequests);
        var approved = central.Approve(request.Code, PairingRoles.Viewer);
        Assert.NotNull(approved);

        var (status, token) = module.Poll(request.Id, secret);
        Assert.Equal(PairStatus.Approved, status);
        Assert.Equal(PairingRoles.Viewer, central.RoleOf(token, "llm"));

        // Genau einmal: danach ist die Anfrage weg
        Assert.Equal(PairStatus.Unknown, module.Poll(request.Id, secret).Status);
        Assert.Empty(central.PendingRequests);
    }

    [Fact]
    public void Poll_needs_the_secret_of_the_request()
    {
        var service = Service();
        var (request, _) = service.RequestPairing("tv", null, "llm")!.Value;
        service.Approve(request.Code);

        Assert.Equal((PairStatus.Unknown, null), service.Poll(request.Id, "wrong-secret"));
        Assert.Equal((PairStatus.Unknown, null), service.Poll("missing", "wrong-secret"));
    }

    [Fact]
    public void A_restarted_module_cannot_hand_out_the_token_of_an_old_request()
    {
        var (request, secret) = Service().RequestPairing("tv", null, "llm")!.Value;
        Service().Approve(request.Code);

        // Der Prozess, der das Token hielt, gibt es nicht mehr
        var (status, token) = Service().Poll(request.Id, secret);

        Assert.Equal(PairStatus.Expired, status);
        Assert.Null(token);
    }

    [Fact]
    public void Requests_can_be_denied_expire_and_are_limited()
    {
        var service = Service();
        var (denied, deniedSecret) = service.RequestPairing("a", null, "llm")!.Value;
        Assert.True(service.Deny(denied.Id));
        Assert.Equal(PairStatus.Denied, service.Poll(denied.Id, deniedSecret).Status);

        for (int i = 0; i < PairingCodes.MaxPendingRequests; i++)
            Assert.NotNull(service.RequestPairing("r" + i, null, "llm"));
        Assert.Null(service.RequestPairing("one too many", null, "llm"));

        var (late, lateSecret) = (service.PendingRequests[0], "");
        _clock.Advance(PairingCodes.CodeLifetime);
        Assert.Null(service.Approve(late.Code));
        Assert.Empty(service.PendingRequests);
        Assert.Equal(PairStatus.Unknown, service.Poll(late.Id, lateSecret).Status);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(service.Maintain());
        Assert.NotNull(service.RequestPairing("room again", null, "llm"));
    }

    // ── Hosts ──

    [Fact]
    public void A_host_pairs_with_the_host_code_and_is_found_by_its_token()
    {
        var service = Service();
        var code = service.HostCode;

        var paired = service.TryPairHost(code, "gpu-box");

        Assert.NotNull(paired);
        Assert.Equal("gpu-box", service.FindHost(paired.Value.Token)?.Name);
        Assert.Null(service.TryPairHost(code, "again"));          // verbraucht
        Assert.Single(service.Hosts);
        Assert.Empty(service.Devices);                             // Hosts stehen nicht in der Geräteliste
        Assert.Null(service.RoleOf(paired.Value.Token, "llm"));    // und haben keine Rolle in der Oberfläche
    }

    [Fact]
    public void A_device_token_is_not_a_host_token()
    {
        var service = Service();
        var token = service.Pair(service.Code, "phone", null, "llm")!.Value.Token;

        Assert.Null(service.FindHost(token));
    }

    [Fact]
    public void Five_wrong_host_codes_replace_the_host_code()
    {
        var service = Service();
        var code = service.HostCode;
        var wrong = code == "000000" ? "111111" : "000000";

        for (int i = 0; i < PairingCodes.MaxWrongTries; i++) Assert.Null(service.TryPairHost(wrong, "x"));

        Assert.NotEqual(code, service.HostCode);
    }

    [Fact]
    public void Removing_a_host_does_not_touch_devices()
    {
        var service = Service();
        var device = service.Pair(service.Code, "phone", null, "llm")!.Value;
        var host = service.AddHost("gpu-box");

        Assert.False(service.RemoveDevice(host.Host.Id));   // ein Host ist kein Gerät der Liste
        Assert.True(service.RemoveHost(host.Host.Id));

        Assert.Null(service.FindHost(host.Token));
        Assert.NotNull(service.RoleOf(device.Token, "llm"));
    }

    [Fact]
    public void The_remote_switch_is_shared()
    {
        Service().SetRemote(true);

        Assert.True(Service().RemoteEnabled);
    }
}
