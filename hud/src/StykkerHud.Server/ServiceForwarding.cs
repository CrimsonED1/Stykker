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

// Ein fremder Dienst, den die Zentrale weiterleitet: sein Port hier, sein Ziel und das Token, das die Zentrale ihm
// gibt. Start ist der Start-Eintrag aus stykker.json (oder null, wenn ein anderer ihn schon betreibt).
internal sealed record ServiceRoute(string Id, string Name, int Port, string Target, string Token, HubStart? Start);

// Die Weiterleitung fremder Dienste (docs/plan-stykkernode.md, Abschnitt 4.5 bis 4.7): je Dienst ein eigener Port, auf dem
// die Zentrale jede Anfrage mit YARP an das Ziel weitergibt. Sie steht vor der Zugangsprüfung der Zentrale: die prüft
// hier selbst (ServiceProxyRules), und ohne das sähe jede Anfrage von diesem Rechner wie Loopback aus. Die Regeln stehen
// in Stykker.Shared; hier nur der Weg der Anfrage durch YARP und Kestrel.
internal static class ServiceForwarding
{
    // Die Routen aus den Modulen. Die Tokens entstehen nur, wenn es Weiterleitungen gibt: sonst bleibt services.dat unberührt.
    public static IReadOnlyList<ServiceRoute> Plan(IReadOnlyList<HubModule> modules, string tokenFile, int hubPort, Action<string> log)
    {
        var forwarded = modules.Where(m => m.External && m.Port is > 0 && m.Target != null).ToList();
        if (forwarded.Count == 0) return [];

        var tokens = new ServiceTokens(tokenFile);
        var routes = new List<ServiceRoute>();
        foreach (var module in forwarded)
        {
            if (module.Port == hubPort || routes.Any(r => r.Port == module.Port))
            {
                log($"{module.Name}: port {module.Port} is already in use by the hub or another service; not forwarded.");
                continue;
            }
            routes.Add(new ServiceRoute(module.Id, module.Name, module.Port!.Value, module.Target!, tokens.GetOrCreate(module.Id), module.Start));
            log($"{module.Name}: port {module.Port} forwards to {module.Target}.");
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
    public static void Use(WebApplication app, PairingService pairing, IReadOnlyList<ServiceRoute> routes, int hubPort)
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

        // Der Name, unter dem dieser PC erreichbar ist: nur an ihm gilt ein Host-Kopf (ServiceProxyRules.HostAllowed)
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
                machineName, machineDomain);
            if (access != ServiceAccess.Allow)
            {
                context.Response.StatusCode = ServiceProxyRules.StatusOf(access);
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(ServiceProxyRules.MessageOf(access) ?? "");
                return;
            }

            await forwarder.SendAsync(context, route.Target, client, ForwarderRequestConfig.Empty, new Transformer(route, hubPort));
        });
    }

    // Was die Weiterleitung an Anfrage und Antwort ändert (ServiceProxyRules, Abschnitt 4.7). Der Körper wird nicht gelesen:
    // Antworten strömen unverändert durch.
    private sealed class Transformer(ServiceRoute route, int hubPort) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken cancel)
        {
            await base.TransformRequestAsync(context, request, destinationPrefix, cancel);

            // Der Dienst lässt nur seinen eigenen Host-Namen zu
            request.Headers.Host = ServiceProxyRules.TargetHost(route.Target);

            var origin = ServiceProxyRules.OriginFor(context.Request.Headers.Origin.ToString(), route.Target);
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
                context.Request.Headers["Sec-WebSocket-Protocol"].ToString(), route.Token);
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
