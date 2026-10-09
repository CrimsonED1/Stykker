using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// Kopplung von der Seite des Hosts (P3): die Anfrage an den Server und eine kleine Seite im Browser, um Server und Code
// zu wählen. Die Seite läuft nur auf diesem PC (localhost) und nur unter einer zufälligen Adresse, die der Tray öffnet –
// eine fremde Webseite kann sie also nicht aufrufen.
public static class HostPairClient
{
    public sealed record Result(bool Ok, string Message, string Token = "", string ServerName = "", string Id = "");

    public static async Task<Result> PairAsync(string serverUrl, string code, string name, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        Uri baseUri;
        try { baseUri = new Uri(serverUrl.Trim().TrimEnd('/') + "/"); }
        catch (UriFormatException) { return new Result(false, Strings.HostPairBadUrl); }
        if (baseUri.Scheme is not ("http" or "https")) return new Result(false, Strings.HostPairBadUrl);
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            using var resp = await http.PostAsJsonAsync(new Uri(baseUri, "hosts/pair"), new Dictionary<string, string> { ["code"] = code, ["name"] = name }, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
            var r = doc.RootElement;
            if (resp.IsSuccessStatusCode && J.Str(r, "token") is { Length: > 0 } token)
                return new Result(true, Strings.HostPairDone(J.Str(r, "serverName") ?? baseUri.Host), token, J.Str(r, "serverName") ?? "", J.Str(r, "id") ?? "");
            return new Result(false, J.Str(r, "message") ?? Strings.HostPairFailed(((int)resp.StatusCode).ToString(Strings.Inv)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new Result(false, Strings.HostPairUnreachable(ex.Message));
        }
    }
}

// Die kleine Kopplungsseite des Hosts (HttpListener auf localhost, zufälliger Port und zufälliger Pfad)
public sealed class HostPairPage : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    private readonly Func<string, string, Task<HostPairClient.Result>> _pair;
    private readonly Func<CancellationToken, Task<List<DiscoveredNode>>> _search;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string Url { get; private set; } = "";

    // pair(serverUrl, code) koppelt und speichert; search sucht Server im Netz
    public HostPairPage(Func<string, string, Task<HostPairClient.Result>> pair, Func<CancellationToken, Task<List<DiscoveredNode>>>? search = null)
    {
        _pair = pair;
        _search = search ?? (ct => NodeDiscovery.SearchAsync(TimeSpan.FromSeconds(2), ct));
    }

    public void Start()
    {
        if (_loop != null) return;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int port = RandomNumberGenerator.GetInt32(49152, 65000);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{port}/{_secret}/");
                _listener.Start();
                Url = $"http://localhost:{port}/{_secret}/";
                break;
            }
            catch (HttpListenerException) { /* Port belegt: anderer */ }
        }
        if (Url.Length == 0) throw new InvalidOperationException("no free local port for the pairing page");
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var rest = path.StartsWith("/" + _secret, StringComparison.Ordinal) ? path[(_secret.Length + 1)..].Trim('/') : null;
            if (rest == "" && ctx.Request.HttpMethod == "GET") await Send(ctx, 200, "text/html", Html.Form());
            else if (rest == "search" && ctx.Request.HttpMethod == "GET")
            {
                var found = await _search(_cts.Token).ConfigureAwait(false);
                await Send(ctx, 200, "application/json", JsonSerializer.Serialize(found.Select(f => new { name = f.Name, url = f.Url, remote = f.Remote, self = f.Self })));
            }
            else if (rest == "pair" && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var form = ParseForm(await reader.ReadToEndAsync().ConfigureAwait(false));
                var result = await _pair(form.GetValueOrDefault("server") ?? "", form.GetValueOrDefault("code") ?? "").ConfigureAwait(false);
                await Send(ctx, result.Ok ? 200 : 400, "text/html", Html.Outcome(result));
            }
            else await Send(ctx, 404, "text/plain", "not found");
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
    }

    internal static Dictionary<string, string> ParseForm(string body)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[WebUtility.UrlDecode(kv[0])] = kv.Length > 1 ? WebUtility.UrlDecode(kv[1]) : "";
        }
        return d;
    }

    private static async Task Send(HttpListenerContext ctx, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type + "; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
    }

    // Die Seite: Server aus der Suche (Klick füllt die Adresse) oder von Hand, dazu der Code
    private static class Html
    {
        private static string Esc(string s) => WebUtility.HtmlEncode(s);
        private const string Style = "body{margin:0;background:#05070d;color:#dce9fa;font:15px Segoe UI,system-ui,sans-serif;display:flex;justify-content:center;padding:40px 16px}"
            + ".box{background:#0c1320;border:1px solid #223350;border-radius:12px;padding:22px;max-width:520px;width:100%}"
            + "h1{font-size:17px;letter-spacing:.06em;margin:0 0 6px}h1 b{color:#6cb8ff}p{color:#6f86a6;margin:6px 0 14px}"
            + "label{display:block;color:#6f86a6;font-size:12px;margin:12px 0 4px}input{width:100%;box-sizing:border-box;background:#0f1a2c;color:#dce9fa;border:1px solid #1b2b40;border-radius:8px;padding:9px 10px;font:inherit}"
            + "input.code{font:22px Cascadia Mono,Consolas,monospace;letter-spacing:.2em}button{margin-top:16px;background:#6cb8ff;color:#05070d;border:0;border-radius:999px;padding:9px 22px;font:inherit;font-weight:600;cursor:pointer}"
            + ".srv{display:block;width:100%;text-align:left;margin:6px 0 0;background:#0f1a2c;color:#dce9fa;border:1px solid #223350;font-weight:400}.srv small{color:#6f86a6}"
            + ".ok{color:#6ce8c0}.bad{color:#ff6f8a}";

        public static string Form() => $$$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{{Esc(Strings.HostPairTitle)}}}</title><style>{{{Style}}}</style></head><body><form class="box" method="post" action="pair">
            <h1>◆ STYKKER <b>HOST</b></h1><p>{{{Esc(Strings.HostPairHint)}}}</p>
            <label>{{{Esc(Strings.HostPairFound)}}}</label><div id="found"><p>{{{Esc(Strings.HostPairSearching)}}}</p></div>
            <label for="server">{{{Esc(Strings.HostPairServer)}}}</label><input id="server" name="server" placeholder="http://192.168.1.10:17400" required>
            <label for="code">{{{Esc(Strings.HostPairCode)}}}</label><input id="code" class="code" name="code" inputmode="numeric" maxlength="7" placeholder="123456" required>
            <button type="submit">{{{Esc(Strings.HostPairButton)}}}</button></form>
            <script>
            fetch('search').then(r=>r.json()).then(list=>{const f=document.getElementById('found');f.innerHTML='';
            if(!list.length){f.innerHTML='<p>{{{Esc(Strings.HostPairNoneFound)}}}</p>';return}
            for(const s of list){const b=document.createElement('button');b.type='button';b.className='srv';b.innerHTML='';b.append(s.name+'  ');
            const sm=document.createElement('small');sm.textContent=s.url+(s.remote?'':' · {{{Esc(Strings.HostPairRemoteOff)}}}');b.append(sm);
            b.onclick=()=>{document.getElementById('server').value=s.url;document.getElementById('code').focus()};f.append(b)}}).catch(()=>{});
            </script></body></html>
            """;

        public static string Outcome(HostPairClient.Result r) => $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>{{Esc(Strings.HostPairTitle)}}</title><style>{{Style}}</style></head><body>
            <div class="box"><h1>◆ STYKKER <b>HOST</b></h1><p class="{{(r.Ok ? "ok" : "bad")}}">{{Esc(r.Message)}}</p>
            {{(r.Ok ? "<p>" + Esc(Strings.HostPairClose) + "</p>" : "<p><a href=\".\" style=\"color:#6cb8ff\">" + Esc(Strings.HostPairAgain) + "</a></p>")}}</div></body></html>
            """;
    }
}
