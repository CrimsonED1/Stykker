using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

// Der Tausch gegen ein Geräte-Token, gegen einen Ersatz-Server auf 127.0.0.1 (er spricht den Weg von Qwen Local Control
// nach: Bearer-Geheimnis, Körper {} oder {previousToken}, Antwort mit token oder reused). Das Geheimnis ist ein Platzhalter.
public class LocalControlExchangeTests
{
    private const string Secret = "placeholder-pairing-secret-123";

    // ── Die URL aus Qwen ──

    [Theory]
    [InlineData("http://192.0.2.10:4170/#token=abc123", "http://192.0.2.10:4170", "abc123")]
    [InlineData("  http://192.0.2.10:4170/#token=abc123  ", "http://192.0.2.10:4170", "abc123")]
    [InlineData("https://pc.example.ts.net:4170/#token=abc-_123", "https://pc.example.ts.net:4170", "abc-_123")]
    [InlineData("http://192.0.2.10:80/#token=abc", "http://192.0.2.10:80", "abc")]
    public void A_local_control_url_gives_its_address_and_secret(string url, string baseUrl, string secret)
    {
        Assert.True(LocalControlExchange.TryParseUrl(url, out var address, out var found));
        Assert.Equal(baseUrl, address);
        Assert.Equal(secret, found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://192.0.2.10:4170/#token=abc")]
    [InlineData("http://192.0.2.10/#token=abc")]                 // ohne Port
    [InlineData("http://192.0.2.10:4170/x#token=abc")]           // mit Pfad
    [InlineData("http://192.0.2.10:4170/?a=1#token=abc")]        // mit Suche
    [InlineData("http://192.0.2.10:4170/")]                       // ohne Geheimnis
    [InlineData("http://192.0.2.10:4170/#token=")]               // leeres Geheimnis
    [InlineData("http://192.0.2.10:4170/#pairing=abc")]          // anderer Name
    [InlineData("http://192.0.2.10:4170/#token=a%20b")]          // Leerraum im Geheimnis
    [InlineData("http://user@192.0.2.10:4170/#token=abc")]       // Benutzername
    [InlineData("http://192.0.2.10:4170/#token=abc#more")]
    public void Anything_else_is_not_a_local_control_url(string url)
    {
        Assert.False(LocalControlExchange.TryParseUrl(url, out _, out _));
    }

    // ── Der Tausch ──

    [Fact]
    public async Task The_exchange_trades_the_secret_for_a_device_token_and_names_the_device()
    {
        await using var server = await FakeLocalControl.StartAsync(Mode.Ok, Secret);
        var client = Client();

        var result = await LocalControlExchange.ExchangeAsync(client, server.BaseUrl, Secret, null, default);

        Assert.True(result.Ok);
        Assert.Equal("device-new-token", result.Token);
        Assert.NotEqual(Secret, result.Token);
        Assert.Equal("POST", server.Last!.Method);
        Assert.Equal("/workspace/local-control/devices", server.Last.Path);
        Assert.Equal("Bearer " + Secret, server.Last.Authorization);
        Assert.Equal("Stykker hub", server.Last.UserAgent);
        Assert.Equal("{}", server.Last.Body);
        Assert.Equal($"127.0.0.1:{server.Port}", server.Last.Host);
    }

    [Fact]
    public async Task A_known_device_sends_its_previous_token_and_keeps_it()
    {
        await using var server = await FakeLocalControl.StartAsync(Mode.Ok, Secret);

        var result = await LocalControlExchange.ExchangeAsync(Client(), server.BaseUrl, Secret, "device-old", default);

        Assert.True(result.Ok);
        Assert.Equal("device-old", result.Token);
        Assert.Contains("device-old", server.Last!.Body);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_and_the_message_holds_no_secret()
    {
        await using var server = await FakeLocalControl.StartAsync(Mode.Ok, Secret);

        var result = await LocalControlExchange.ExchangeAsync(Client(), server.BaseUrl, "some-other-secret", null, default);

        Assert.False(result.Ok);
        Assert.Null(result.Token);
        Assert.Contains("does not accept", result.Message);
        Assert.DoesNotContain("some-other-secret", result.Message);
    }

    [Fact]
    public async Task A_device_limit_gets_its_own_message()
    {
        await using var server = await FakeLocalControl.StartAsync(Mode.Limit, Secret);

        var result = await LocalControlExchange.ExchangeAsync(Client(), server.BaseUrl, Secret, null, default);

        Assert.False(result.Ok);
        Assert.Contains("device limit", result.Message);
        Assert.DoesNotContain(Secret, result.Message);
    }

    [Fact]
    public async Task Local_control_switched_off_gets_its_own_message()
    {
        await using var server = await FakeLocalControl.StartAsync(Mode.Off, Secret);

        var result = await LocalControlExchange.ExchangeAsync(Client(), server.BaseUrl, Secret, null, default);

        Assert.False(result.Ok);
        Assert.Contains("not on", result.Message);
    }

    [Fact]
    public async Task An_answer_without_a_token_is_not_a_pairing()
    {
        await using var server = await FakeLocalControl.StartAsync(Mode.NoToken, Secret);

        var result = await LocalControlExchange.ExchangeAsync(Client(), server.BaseUrl, Secret, null, default);

        Assert.False(result.Ok);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task An_address_that_does_not_answer_is_refused_without_a_secret_in_the_message()
    {
        // Ein Port, auf dem nichts hört: erst freigeben, dann anfragen
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var result = await LocalControlExchange.ExchangeAsync(Client(), $"http://127.0.0.1:{port}", Secret, null, default);

        Assert.False(result.Ok);
        Assert.Contains("does not answer", result.Message);
        Assert.DoesNotContain(Secret, result.Message);
    }

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public enum Mode { Ok, Limit, Off, NoToken }

    // Der Ersatz-Server: nimmt die Anfrage an, merkt sie sich und antwortet je nach Modus.
    private sealed class FakeLocalControl : IAsyncDisposable
    {
        private WebApplication? _app;

        public int Port { get; private set; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";
        public Seen? Last { get; private set; }

        public sealed record Seen(string Method, string Path, string? Host, string? Authorization, string? UserAgent, string Body);

        public static async Task<FakeLocalControl> StartAsync(Mode mode, string secret)
        {
            var fake = new FakeLocalControl();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapPost("/workspace/local-control/devices", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                fake.Last = new Seen(context.Request.Method, context.Request.Path, context.Request.Host.Value,
                    context.Request.Headers.Authorization.ToString(), context.Request.Headers.UserAgent.ToString(), body);

                if (context.Request.Headers.Authorization.ToString() != "Bearer " + secret)
                    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
                switch (mode)
                {
                    case Mode.Limit: return Results.Json(new { error = "limit" }, statusCode: 409);
                    case Mode.Off: return Results.Json(new { error = "off" }, statusCode: 404);
                    case Mode.NoToken: return Results.Json(new { id = "d1", rememberDevices = true });
                }
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (json.RootElement.TryGetProperty("previousToken", out var previous) && previous.GetString() == "device-old")
                    return Results.Json(new { id = "d1", reused = true, rememberDevices = true });
                return Results.Json(new { id = "d2", token = "device-new-token", rememberDevices = true });
            });
            await app.StartAsync();
            fake._app = app;
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            fake.Port = new Uri(addresses.First()).Port;
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app != null) await _app.DisposeAsync();
        }
    }
}
