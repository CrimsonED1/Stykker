using System.Net;
using System.Net.Http.Json;
using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Eine Gegenstelle über HTTP: ein anderer StykkerMeshShare-Server.
public sealed class HttpPeerSource(HttpClient http, string address) : IPeerSource
{
    public string Address { get; } = address;
    private Uri Url(string pathAndQuery) => new($"http://{Address}{pathAndQuery}");

    public async Task<Hello> HelloAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<Hello>(Url("/mesh/hello"), ct).ConfigureAwait(false)
        ?? throw new HttpRequestException("empty answer");

    public async Task<ChangePage> GetChangesAsync(long since, CancellationToken ct) =>
        await http.GetFromJsonAsync<ChangePage>(Url($"/mesh/changes?since={since}"), ct).ConfigureAwait(false)
        ?? throw new HttpRequestException("empty answer");

    public async Task<Stream?> OpenFileAsync(string path, string hash, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url($"/mesh/file?path={Uri.EscapeDataString(path)}&hash={hash}"));
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            response.Dispose();
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    // Lange Abfrage: kommt zurück, sobald sich dort etwas ändert, spätestens nach timeout.
    public async Task<long> WaitAsync(long since, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout + TimeSpan.FromSeconds(10));
        var answer = await http.GetFromJsonAsync<Hello>(Url($"/mesh/wait?since={since}&seconds={(int)timeout.TotalSeconds}"), cts.Token).ConfigureAwait(false);
        return answer?.Seq ?? since;
    }
}

public sealed record Hello(string App, string Id, string Name, long Seq);
