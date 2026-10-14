using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stykker.Pairing;
using Stykker.Shared.Config;
using Stykker.Shared.Web;
using Yarp.ReverseProxy.Forwarder;

namespace StykkerHud.Server;

// Wohin eine Anfrage geht: das feste Ziel des Dienstes und das Token, das die Zentrale ihm gibt (null = keins).
internal sealed record ServiceTarget(string Url, string? Token);

// Ein fremder Dienst, den die Zentrale weiterleitet: sein Port hier, sein festes Ziel (target) und optional die Datei,
// aus der die Zentrale sein Token liest. Jede Anfrage prüft neu, ob das Ziel antwortet und das Token lesbar ist.
internal sealed class ServiceRoute
{
    private readonly object _gate = new();
    private readonly Action<string> _log;
    private readonly ServiceTokenFile? _tokenFile;
    private string? _lastProblem;

    public ServiceRoute(string id, string name, int port, string target, ServiceTokenFile? tokenFile, Action<string> log)
    {
        Id = id;
        Name = name;
        Port = port;
        Target = target;
        _tokenFile = tokenFile;
        _log = log;
    }

    public string Id { get; }
    public string Name { get; }
    public int Port { get; }
    public string Target { get; }

    // Das Ziel für eine Anfrage. Antwortet das Ziel nicht, oder ist die Token-Datei nicht lesbar, kommt null und die
    // 503-Antwort mit dem Text dazu. Der Grund geht ins Log, aber nur bei einem Wechsel; das Token nie.
    public async Task<(ServiceTarget? Target, string? Refusal)> ResolveAsync(CancellationToken cancel)
    {
        if (!await HubProbe.AnswersAsync(new HubModule(Id, Name, Port, null, true), Target, cancel))
        {
            Report("the target does not answer");
            return (null, ServiceProxyRules.NotRunningMessage(Name));
        }

        string? token = null;
        if (_tokenFile != null)
        {
            var read = _tokenFile.Current();
            if (read.Token == null)
            {
                Report(read.Problem);
                return (null, ServiceProxyRules.TokenFileMessage(Name));
            }
            token = read.Token;
        }

        Report(null);
        return (new ServiceTarget(Target, token), null);
    }

    private void Report(string? problem)
    {
        lock (_gate)
        {
            if (problem == _lastProblem) return;
            _lastProblem = problem;
            _log(problem == null
                ? $"{Name}: the service answers again."
                : $"{Name}: not reachable: {problem}. Requests get 503 until it is.");
        }
    }
}

// Die Weiterleitung fremder Dienste (docs/plan-stykkernode.md, Abschnitt 4.5 bis 4.7): je Dienst ein eigener Port, auf dem
// die Zentrale jede Anfrage mit YARP an das feste Ziel weitergibt. Sie steht vor der Zugangsprüfung der Zentrale: die prüft
// hier selbst (ServiceProxyRules), und ohne das sähe jede Anfrage von diesem Rechner wie Loopback aus. Die Regeln stehen
// in Stykker.Shared; hier nur der Weg der Anfrage durch YARP und Kestrel. Die Zentrale startet keinen Dienst.
internal static class ServiceForwarding
{
    // Die Routen aus den Modulen: ein fremder Dienst mit Port und Ziel (target), sonst keiner.
    public static IReadOnlyList<ServiceRoute> Plan(IReadOnlyList<HubModule> modules, int hubPort, Action<string> log)
    {
        var routes = new List<ServiceRoute>();
        foreach (var module in modules.Where(m => m.External && m.Port is > 0 && m.Target != null))
        {
            if (module.Start != null) log($"{module.Name}: \"start\" is ignored; the hub no longer starts services.");
            if (module.Port == hubPort || routes.Any(r => r.Port == module.Port))
            {
                log($"{module.Name}: port {module.Port} is already in use by the hub or another service; not forwarded.");
                continue;
            }
            var tokenFile = module.TokenFile == null ? null : new ServiceTokenFile(module.TokenFile.Path, module.TokenFile.Protection);
            routes.Add(new ServiceRoute(module.Id, module.Name, module.Port!.Value, module.Target!, tokenFile, log));
            log($"{module.Name}: port {module.Port} forwards to {module.Target}{(tokenFile == null ? ", without a token." : ", with the token from its token file.")}");
        }
        return routes;
    }

    // Alle Ports, auf die die Zentrale hört: ihr eigener und die der Weiterleitungen. Wie die Zentrale selbst: Netz nur mit Netzschalter.
    public static string[] Urls(int hubPort, bool network, IEnumerable<ServiceRoute> routes) =>
        new[] { hubPort }.Concat(routes.Select(r => r.Port))
            .Select(port => network ? $"http://*:{port}" : $"http://127.0.0.1:{port}")
            .ToArray();

    // Die Middleware vor der Zugangsprüfung der Zentrale: beantwortet alles auf dem Port einer Weiterleitung selbst.
    // Auf anderen Ports geht sie einfach weiter.
    public static void Use(WebApplication app, PairingService pairing, IReadOnlyList<ServiceRoute> routes, int hubPort,
        IReadOnlyCollection<string> extraHostNames)
    {
        if (routes.Count == 0) return;
        var byPort = routes.ToDictionary(r => r.Port);
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        // Ohne Puffer und ohne eigene Cookies, Weiterleitung ohne Umweg über einen Proxy des Systems
        var client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

        // Der Name, unter dem dieser PC erreichbar ist: nur an ihm gilt ein Host-Kopf (ServiceProxyRules.HostAllowed),
        // dazu die Zusatznamen aus stykker.json
        var machineName = Environment.MachineName;
        var machineDomain = IPGlobalProperties.GetIPGlobalProperties().DomainName;

        app.Use(async (context, next) =>
        {
            if (!byPort.TryGetValue(context.Connection.LocalPort, out var route))
            {
                await next();
                return;
            }

            var remote = context.Connection.RemoteIpAddress;
            bool loopback = PairingGate.IsLoopback(remote);
            bool remoteEnabled = !loopback && pairing.RemoteEnabled;
            // Die Rolle des Geräts in diesem Dienst; ohne Netz gibt es kein Gerät zu prüfen
            string? role = remoteEnabled
                ? pairing.RoleOf(ServiceProxyRules.DeviceToken(context.Request.Headers.Cookie.ToString()), route.Id, PairingGate.AddressText(remote))
                : null;
            var access = ServiceProxyRules.Decide(loopback, remoteEnabled, role,
                context.Request.Headers.Origin.ToString(), context.Request.Scheme, context.Request.Host.Value ?? "",
                machineName, machineDomain, extraHostNames);
            if (access != ServiceAccess.Allow)
            {
                context.Response.StatusCode = ServiceProxyRules.StatusOf(access);
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(ServiceProxyRules.MessageOf(access) ?? "");
                return;
            }

            // Läuft der Dienst nicht, oder ist seine Token-Datei nicht lesbar: 503 mit dem Text dazu
            var (target, refusal) = await route.ResolveAsync(context.RequestAborted);
            if (target == null)
            {
                context.Response.StatusCode = ServiceProxyRules.NotRunningStatus;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(refusal ?? "");
                return;
            }

            await forwarder.SendAsync(context, target.Url, client, ForwarderRequestConfig.Empty, new Transformer(target, hubPort));
        });
    }

    // Was die Weiterleitung an Anfrage und Antwort ändert (ServiceProxyRules, Abschnitt 4.7). Der Körper wird nicht gelesen:
    // Antworten strömen unverändert durch.
    private sealed class Transformer(ServiceTarget target, int hubPort) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken cancel)
        {
            await base.TransformRequestAsync(context, request, destinationPrefix, cancel);

            // Der Dienst lässt nur seinen eigenen Host-Namen zu
            request.Headers.Host = ServiceProxyRules.TargetHost(target.Url);

            var origin = ServiceProxyRules.OriginFor(context.Request.Headers.Origin.ToString(), target.Url);
            if (origin != null)
            {
                request.Headers.Remove("Origin");
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            // Das Cookie des Geräts gilt für den Rechnernamen, nicht für den Port. Der Dienst bekommt es nicht.
            request.Headers.Remove("Cookie");
            var cookies = ServiceProxyRules.CookiesWithoutDevice(context.Request.Headers.Cookie.ToString());
            if (cookies != null) request.Headers.TryAddWithoutValidation("Cookie", cookies);

            // Das Token des Dienstes, wenn der Browser keins mitbringt (auch für die Upgrade-Anfrage des WebSockets)
            var bearer = ServiceProxyRules.BearerFor(context.Request.Headers.Authorization.ToString(),
                context.Request.Headers["Sec-WebSocket-Protocol"].ToString(), target.Token);
            if (bearer != null) request.Headers.TryAddWithoutValidation("Authorization", bearer);
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken cancel)
        {
            var ok = await base.TransformResponseAsync(context, response, cancel);

            // Das Verbot, eingebettet zu werden, gilt weiter, nur nicht mehr für die Zentrale unter demselben Rechnernamen
            foreach (var name in ServiceProxyRules.ResponseHeadersToDrop) context.Response.Headers.Remove(name);
            context.Response.Headers.ContentSecurityPolicy = ServiceProxyRules.FrameAncestorsFor(
                context.Response.Headers.ContentSecurityPolicy.ToString(), context.Request.Scheme, context.Request.Host.Value ?? "", hubPort);
            return ok;
        }
    }
}
