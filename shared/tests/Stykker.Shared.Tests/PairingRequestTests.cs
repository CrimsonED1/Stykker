using System.Text.Json;
using Stykker.Pairing;
using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

// Die Kopplung andersherum auf der Anmeldeseite: die Antworten von /pair/request und /pair/poll. Im Körper steht nie ein
// Token; das Cookie setzt der Server (SetDeviceCookie), das ist im HTTP-Lauf zu prüfen.
public class PairingRequestTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_request_answer_has_id_secret_and_the_digits_but_no_token()
    {
        var request = new PairRequest
        {
            Id = "req0001",
            Code = "482913",
            Name = "Chrome on Android",
            Module = "hud",
            Created = Now,
            Expires = Now.AddMinutes(10),
            TokenHash = "TOKEN-HASH-STAYS-IN-THE-STORE",
        };

        var body = PairingGate.RequestBody(request, "secret-value", Now);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("req0001", root.GetProperty("id").GetString());
        Assert.Equal("secret-value", root.GetProperty("secret").GetString());
        Assert.Equal("482 913", root.GetProperty("code").GetString());
        Assert.Equal(600, root.GetProperty("seconds").GetInt32());
        Assert.False(root.TryGetProperty("token", out _));
        Assert.DoesNotContain("TOKEN-HASH", body);
    }

    [Fact]
    public void Seconds_left_never_go_below_zero()
    {
        var request = new PairRequest { Id = "req0002", Code = "000001", Expires = Now };

        using var json = JsonDocument.Parse(PairingGate.RequestBody(request, "s", Now.AddMinutes(1)));

        Assert.Equal(0, json.RootElement.GetProperty("seconds").GetInt32());
    }

    [Theory]
    [InlineData(PairStatus.Pending)]
    [InlineData(PairStatus.Approved)]
    [InlineData(PairStatus.Denied)]
    [InlineData(PairStatus.Expired)]
    [InlineData(PairStatus.Unknown)]
    public void The_poll_answer_is_only_the_status(string status)
    {
        using var json = JsonDocument.Parse(PairingGate.PollBody(status));
        var root = json.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("token", out _));
    }

    [Theory]
    [InlineData("/pair/request")]
    [InlineData("/pair/poll")]
    public void The_reverse_pairing_paths_are_the_sign_in_and_only_open_with_the_switch_on(string path)
    {
        Assert.Equal(GateAction.Pair, PairingGate.Decide(loopback: false, remoteEnabled: true, path, wantsPage: false, role: null));
        Assert.Equal(GateAction.Forbidden, PairingGate.Decide(loopback: false, remoteEnabled: false, path, wantsPage: false, role: null));
    }
}
