using System.Net;
using System.Text;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Server;

// Wer darf was (S2). Drei Wege hinein:
//  1. Der Schlüssel aus dem Datenordner (server.key, an den Benutzer gebunden) – Fenster, TUI und Skripte auf diesem Rechner.
//     Eine Webseite im Browser kann ihn nicht lesen; ein Schreibzugriff aus dem Browser kommt so nicht durch.
//  2. Das Gerätetoken – als Cookie (Browser, Handy) oder als Kopf X-Stykker-Device (StykkerUI).
//  3. Ohne alles: nur /api/ping (für die Erkennung) und /pair… (die Anmeldung selbst).
// Zusätzlich: von außerhalb nur, wenn der Schalter Home/VPN an ist; und der Host-Kopf muss zur eigenen Adresse passen
// (sonst könnte eine fremde Webseite über ihren Domainnamen auf 127.0.0.1 zugreifen).
//
// Anmelden geht in beide Richtungen, jeweils mit sechs Ziffern (AccessControl):
//  /pair?code=…           der PC zeigt den Code, das neue Gerät bringt ihn mit (QR-Code oder abgetippt)
//  /pair/request          das neue Gerät holt sich einen eigenen Code und zeigt ihn; ein angemeldetes Gerät gibt frei
//  /pair/poll             … und das neue Gerät fragt nach, bis das Token bereitliegt
//  /pair/token            wie /pair?code=…, aber mit JSON-Antwort (Programme, die den Code kennen)
public sealed class AccessGate(AccessControl access, string key, int port, Func<ThemeInfo>? theme = null)
{
    // Rolle des angemeldeten Aufrufers. Der Schlüssel aus dem Datenordner ist immer Admin (Fenster, TUI, Skripte),
    // ein Gerätetoken bringt die Rolle aus access.dat mit.
    public const string RoleItem = "StykkerLlm.Role";

    public AccessControl Access => access;
    public int Port => port;

    // Was der Server über diesen Aufrufer wissen muss, nachdem die Anmeldung durch ist
    public static string RoleOf(HttpContext ctx) => ctx.Items[RoleItem] as string ?? AccessRole.Admin;

    // Das Theme des Fensters, damit die Anmeldeseite genauso aussieht wie der Rest (sie wird ohne Stylesheet
    // ausgeliefert: ein Browser ohne Sitzung darf ja gar keine Blazor-Anwendung starten)
    private ThemeInfo Theme => theme?.Invoke() ?? ThemeCatalog.Default;

    public async Task<bool> TryHandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var remote = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
        var loopback = NetAddr.IsLoopback(remote.ToString());

        // 1) Erkennung: läuft der Server, und ist er fürs Heimnetz freigegeben? (der Name hilft beim Koppeln eines Hosts)
        if (path.StartsWith("/api/ping", StringComparison.OrdinalIgnoreCase))
        {
            await Json(ctx, StatusCodes.Status200OK,
                $"{{\"ok\":true,\"app\":\"StykkerLLM-Server\",\"port\":{port},\"remote\":{Json(access.RemoteEnabled)},\"needsCode\":true," +
                $"\"name\":{JsonSerializer.Serialize(Environment.MachineName)}}}");
            return false;
        }

        // 2) Anmeldung. Von außen nur mit Home/VPN – sonst endet jeder Versuch ohnehin am nächsten Schritt,
        //    und die Anfragen sollen sich nicht von außen anlegen lassen.
        if (path.StartsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            if (!loopback && !access.RemoteEnabled)
            {
                await Text(ctx, StatusCodes.Status403Forbidden, Strings.RemoteOff + "\n\n" + Strings.RemoteHint);
                return true;
            }
            var sub = path.Length > 5 ? path[5..].TrimEnd('/').ToLowerInvariant() : "";
            switch (sub)
            {
                case "/request": await RequestAsync(ctx); break;
                case "/poll": await PollAsync(ctx); break;
                case "/token": await TokenAsync(ctx); break;
                case "/forget": await ForgetAsync(ctx); break;
                case "/local": await LocalAsync(ctx); break;
                case "/adopt": await AdoptAsync(ctx); break;
                default: await PairAsync(ctx); break;
            }
            return true;
        }

        // 2b) Model-Hosts wählen sich ein (WebSocket mit Host-Token, geprüft am Endpunkt). Von außen nur mit Home/VPN.
        if (path.StartsWith("/hosts/", StringComparison.OrdinalIgnoreCase))
        {
            if (!loopback && !access.RemoteEnabled)
            {
                await Text(ctx, StatusCodes.Status403Forbidden, Strings.RemoteOff);
                return true;
            }
            return false;
        }

        // 3) Die eigenen Messwerte (/api/metrics, see docs/ui.md) brauchen keinen Schlüssel – aber nur von diesem Rechner.
        // Inhalt sind ausschließlich Messwerte: kein Zugangscode, kein Schlüssel, keine Datei. Die Ausnahme gilt nur
        // für genau diesen Pfad und nur auf Loopback; von außen bleibt alles Weitere oben (Home/VPN + Code).
        if (path.StartsWith("/api/metrics", StringComparison.OrdinalIgnoreCase) && loopback)
            return false;

        // 4) Von außen nur mit Schalter
        if (!loopback && !access.RemoteEnabled)
        {
            await Text(ctx, StatusCodes.Status403Forbidden, Strings.RemoteOff + "\n\n" + Strings.RemoteHint);
            return true;
        }

        // 5) Host-Kopf prüfen (nur wenn nur dieser PC erreichbar ist): verhindert Zugriff über einen fremden Domainnamen
        if (!access.RemoteEnabled && !HostMatchesSelf(ctx.Request.Host))
        {
            await Text(ctx, StatusCodes.Status403Forbidden, "Wrong host name. Open http://" + NetAddr.Url("127.0.0.1", port));
            return true;
        }

        var who = Auth(access, ctx, key);
        if (who != null)
        {
            ctx.Items[RoleItem] = who.Value.Role;
            return false;   // weiter
        }

        // 6) Ohne Berechtigung: Webseite zur Anmeldung, API-Antwort mit 401
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await Json(ctx, StatusCodes.Status401Unauthorized, $$"""{"ok":false,"message":{{JsonSerializer.Serialize(Strings.RemoteCodeWrong)}}}""");
            return true;
        }
        ctx.Response.Redirect("/pair");
        return true;
    }

    // null = nicht angemeldet. Der Schlüssel aus dem Datenordner ist immer Admin; beim Gerätetoken zählt die Rolle
    // aus access.dat (ein Viewer darf alles lesen, aber nichts verändern).
    private static (string Role, string Kind)? Auth(AccessControl access, HttpContext? ctx, string key = "")
    {
        if (ctx == null) return null;
        if (ctx.Request.Headers.TryGetValue(StateJson.KeyHeader, out var k) && k.ToString() == key) return (AccessRole.Admin, "key");
        var address = ctx.Connection.RemoteIpAddress?.ToString();
        if (ctx.Request.Headers.TryGetValue(StateJson.DeviceHeader, out var dev) && access.RoleOf(dev.ToString(), address) is { } hubRole)
            return (hubRole, "device");
        var token = ctx.Request.Cookies[StateJson.CookieName];
        var role = string.IsNullOrEmpty(token) ? null : access.RoleOf(token, address);
        return role == null ? null : (role, "cookie");
    }

    private static bool HostMatchesSelf(HostString host)
    {
        var value = host.Value ?? "";
        if (value.Length == 0) return true;                       // HTTP/1.0 ohne Host
        var name = value.Contains(':') && !value.StartsWith('[') ? value[..value.IndexOf(':')] : value;
        return name is "127.0.0.1" or "localhost" or "::1" or "[::1]" or "0.0.0.0";
    }

    private static string DeviceName(HttpContext ctx, string? given)
    {
        if (!string.IsNullOrWhiteSpace(given)) return given;
        var name = ctx.Request.Headers.UserAgent.ToString().Split('/')[0];
        return string.IsNullOrWhiteSpace(name) ? "Browser" : name;
    }

    private static void SetCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(StateJson.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(2),
            Secure = false,   // kein HTTPS vorerst (docs/architecture.md)
        });

    // Formularfelder oder Query – beides geht (das Formular schickt GET, Programme POST)
    private static async Task<Dictionary<string, string>> ArgsAsync(HttpContext ctx)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in ctx.Request.Query) d[q.Key] = q.Value.ToString();
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            if (ctx.Request.HasFormContentType)
                foreach (var f in await ctx.Request.ReadFormAsync(ctx.RequestAborted)) d[f.Key] = f.Value.ToString();
            else
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString() ?? "";
                }
                catch (JsonException) { /* leerer oder kaputter Körper: nur die Query zählt */ }
            }
        }
        return d;
    }

    private static string Arg(Dictionary<string, string> d, string name) => d.TryGetValue(name, out var v) ? v : "";

    // /pair?code=… (aus dem QR-Code) meldet das Gerät an und legt das Cookie; ohne Code die Anmeldeseite
    private async Task PairAsync(HttpContext ctx)
    {
        var args = await ArgsAsync(ctx);
        var code = Arg(args, "code");
        if (!string.IsNullOrWhiteSpace(code))
        {
            var paired = access.Pair(code, DeviceName(ctx, Arg(args, "name")), ctx.Connection.RemoteIpAddress?.ToString());
            if (paired == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(PairPage(IsLocal(ctx), Strings.RemoteCodeWrong));
                return;
            }
            SetCookie(ctx, paired.Value.Token);
            // Das Fenster öffnet "/pair?code=…&next=bench", damit der Browser nach der Anmeldung dort landet
            var next = Arg(args, "next");
            ctx.Response.Redirect(next.Length > 0 && next.All(c => char.IsLetterOrDigit(c) || c is '/' or '-' or '_') ? "/" + next : "/");
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(PairPage(IsLocal(ctx), ""));
    }

    // Das neue Gerät holt sich einen eigenen Code (Netflix-Weg). Antwort: Id und Secret zum Nachfragen, der Code
    // zum Anzeigen und ein QR-Code, den ein angemeldetes Handy scannen kann (/approve?code=…).
    private async Task RequestAsync(HttpContext ctx)
    {
        var args = await ArgsAsync(ctx);
        var made = access.RequestPairing(DeviceName(ctx, Arg(args, "name")), Arg(args, "kind"), ctx.Connection.RemoteIpAddress?.ToString());
        if (made == null)
        {
            await Json(ctx, StatusCodes.Status429TooManyRequests, $$"""{"ok":false,"message":{{JsonSerializer.Serialize(Strings.PairTooMany)}}}""");
            return;
        }
        var (req, secret) = made.Value;
        var host = ctx.Request.Host.HasValue ? ctx.Request.Host.Value : NetAddr.Url("127.0.0.1", port);
        var approveUrl = $"http://{host}/approve?code={req.Code}";
        string qr;
        try { qr = QrCode.Encode(approveUrl).ToSvg(5, 3, "#0b0b0b", "#ffffff"); } catch { qr = ""; }
        await Json(ctx, StatusCodes.Status200OK, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true, ["id"] = req.Id, ["secret"] = secret, ["code"] = req.Code, ["pretty"] = AccessControl.Pretty(req.Code),
            ["expires"] = req.Expires.ToString("o"), ["seconds"] = (int)AccessControl.CodeLifetime.TotalSeconds,
            ["approveUrl"] = approveUrl, ["qr"] = qr,
        }));
    }

    // Nachfragen: Status, und nach der Freigabe genau einmal das Token. Ein Browser (cookie=1) bekommt es als Cookie
    // und nicht in den Körper – so kommt kein Skript der Seite an das Token.
    private async Task PollAsync(HttpContext ctx)
    {
        var args = await ArgsAsync(ctx);
        var (status, token) = access.Poll(Arg(args, "id"), Arg(args, "secret"));
        string body;
        if (token != null && Arg(args, "cookie") == "1")
        {
            SetCookie(ctx, token);
            body = $$"""{"ok":true,"status":"{{status}}"}""";
        }
        else
            body = token == null
                ? $$"""{"ok":true,"status":"{{status}}"}"""
                : $$"""{"ok":true,"status":"{{status}}","token":{{JsonSerializer.Serialize(token)}}}""";
        await Json(ctx, StatusCodes.Status200OK, body);
    }

    // Anmelden mit dem Code dieses PCs, Antwort als JSON (ein Programm, dem man den Code gesagt hat)
    private async Task TokenAsync(HttpContext ctx)
    {
        var args = await ArgsAsync(ctx);
        var paired = access.Pair(Arg(args, "code"), DeviceName(ctx, Arg(args, "name")), ctx.Connection.RemoteIpAddress?.ToString(), Arg(args, "kind"));
        if (paired == null)
        {
            await Json(ctx, StatusCodes.Status403Forbidden, $$"""{"ok":false,"message":{{JsonSerializer.Serialize(Strings.RemoteCodeWrong)}}}""");
            return;
        }
        await Json(ctx, StatusCodes.Status200OK, $$"""{"ok":true,"token":{{JsonSerializer.Serialize(paired.Value.Token)}},"name":{{JsonSerializer.Serialize(Environment.MachineName)}}}""");
    }

    // Das Fenster (StykkerUI) holt sich ein eigenes Gerät – nur mit dem Schlüssel des Datenordners und nur
    // von diesem PC. So braucht sie den Zugangscode nicht (der ist für Handys da und nach einer Anmeldung verbraucht).
    private async Task LocalAsync(HttpContext ctx)
    {
        bool keyOk = ctx.Request.Headers.TryGetValue(StateJson.KeyHeader, out var k) && k.ToString() == key;
        if (!HttpMethods.IsPost(ctx.Request.Method) || !IsLocal(ctx) || !keyOk)
        {
            await Json(ctx, StatusCodes.Status403Forbidden, """{"ok":false}""");
            return;
        }
        var args = await ArgsAsync(ctx);
        var (token, _) = access.AddLocalDevice(DeviceName(ctx, Arg(args, "name")), ctx.Connection.RemoteIpAddress?.ToString());
        await Json(ctx, StatusCodes.Status200OK, $$"""{"ok":true,"token":{{JsonSerializer.Serialize(token)}}}""");
    }

    // … und legt es in ihrer WebView als Cookie ab (ein Formular, das sie selbst abschickt). Nur von diesem PC, nur ein
    // gültiges Token; danach weiter zur gewünschten Seite.
    private async Task AdoptAsync(HttpContext ctx)
    {
        var args = await ArgsAsync(ctx);
        var token = Arg(args, "token");
        var next = Arg(args, "next");
        var target = next.Length > 0 && next.All(c => char.IsLetterOrDigit(c) || c is '/' or '-' or '_') ? "/" + next.TrimStart('/') : "/";
        if (!HttpMethods.IsPost(ctx.Request.Method) || !IsLocal(ctx) || access.RoleOf(token, ctx.Connection.RemoteIpAddress?.ToString()) == null)
        {
            ctx.Response.Redirect("/pair");
            return;
        }
        SetCookie(ctx, token);
        ctx.Response.Redirect(target);
    }

    // Ein Gerät meldet sich ab: sein eigenes Token aus der Geräteliste streichen (nur das, mit dem er kommt)
    private async Task ForgetAsync(HttpContext ctx)
    {
        var token = ctx.Request.Headers.TryGetValue(StateJson.DeviceHeader, out var dev) ? dev.ToString() : ctx.Request.Cookies[StateJson.CookieName];
        var removed = HttpMethods.IsPost(ctx.Request.Method) && access.RemoveToken(token);
        await Json(ctx, StatusCodes.Status200OK, removed ? """{"ok":true}""" : """{"ok":false}""");
    }

    // Nur ein Aufruf von diesem PC (Loopback, ohne weitergereichte Adresse) darf den Zugangscode sehen.
    // Von einem anderen Gerät aus wäre die Anmeldeseite sonst ein Schlüssel zum Selbstabholen.
    private static bool IsLocal(HttpContext ctx) =>
        NetAddr.IsLoopback((ctx.Connection.RemoteIpAddress ?? IPAddress.None).ToString()) && !ctx.Request.Headers.ContainsKey("X-Forwarded-For");

    // Das Formular ist absichtlich schlicht: ohne Anmeldung darf keine Blazor-Anwendung starten.
    // Auf diesem PC: QR-Code zum Scannen, der Code zum Abtippen und „In diesem Browser fortfahren“.
    // Von einem anderen Gerät: das Eingabefeld für die sechs Ziffern des PCs – oder andersherum einen eigenen Code
    // zeigen, den man am PC (oder mit einem angemeldeten Handy) freigibt.
    private string PairPage(bool local, string error)
    {
        var th = Theme;
        var form = "<form method=\"get\" action=\"/pair\"><input name=\"code\" inputmode=\"numeric\" pattern=\"[0-9 ]*\" maxlength=\"7\" placeholder=\"" +
                   Esc(Strings.PairApprovePlaceholder) + "\" autocomplete=\"one-time-code\"" + (local ? "" : " autofocus") + "><button>OK</button></form>";
        var err = error.Length > 0 ? $"<p class=\"bad\">{Esc(error)}</p>" : "";
        string body;
        if (local && access.Code.Length > 0)
        {
            var code = access.Code;
            var url = NetInfo.PairUrl(port, code, access.RemoteEnabled ? null : "127.0.0.1");
            string qr;
            try { qr = QrCode.Encode(url).ToSvg(5, 3, "#0b0b0b", "#ffffff"); } catch { qr = ""; }
            body = $"<h2>{Esc(Strings.RemoteTitle)}</h2>" + err +
                   (access.RemoteEnabled
                       ? $"<p class=\"muted\">{Esc(Strings.RemoteScan)}</p><div class=\"qr\">{qr}</div>"
                       : $"<p class=\"warn\">{Esc(Strings.RemoteOff)}</p><p class=\"muted small\">{Esc(Strings.RemoteHint)}</p>") +
                   $"<p class=\"code\">{Esc(AccessControl.Pretty(code))}</p>" +
                   $"<p class=\"muted small\">{Esc(Strings.CodeValidFor(access.CodeExpires - DateTime.Now))}</p>" +
                   $"<a class=\"btn\" href=\"/pair?code={Uri.EscapeDataString(code)}&amp;name={Uri.EscapeDataString("This PC")}\">{Esc(Strings.RemoteThisBrowser)}</a>";
        }
        else
        {
            // Andersherum: Knopf holt per Skript einen eigenen Code, zeigt ihn mit QR und fragt nach, bis freigegeben ist.
            // Das Skript steht inline: die Seite kommt ohne weitere Dateien aus.
            body = $"<h2>{Esc(Strings.RemoteTitle)}</h2>" + err + $"<p class=\"muted\">{Esc(Strings.RemoteEnterCode)}</p>" + form +
                   $"<div class=\"sep\">{Esc(Strings.PairOtherWay)}</div>" +
                   $"<p class=\"muted small\">{Esc(Strings.PairShowCodeHint)}</p>" +
                   $"<div id=\"rq\"><button type=\"button\" class=\"wide\" onclick=\"rq()\">{Esc(Strings.PairShowCode)}</button></div>" +
                   "<script>" +
                   "const T=" + JsonSerializer.Serialize(new { waiting = Strings.PairWaiting, denied = Strings.PairDenied, expired = Strings.PairExpired, scan = Strings.PairScanToApprove }) + ";" +
                   "async function rq(){const box=document.getElementById('rq');" +
                   "const r=await fetch('/pair/request',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({kind:'browser'})});" +
                   "const j=await r.json();if(!j.ok){box.innerHTML='<p class=bad></p>';box.firstChild.textContent=j.message;return;}" +
                   "box.innerHTML='<p class=code></p><p class=\"muted small\" id=st></p><p class=\"muted small\"></p><div class=qr></div>';" +
                   "box.children[0].textContent=j.pretty;box.children[2].textContent=T.scan;box.children[3].innerHTML=j.qr;" +
                   "const end=Date.now()+j.seconds*1000;const st=document.getElementById('st');" +
                   "const tick=async()=>{const left=Math.max(0,end-Date.now());st.textContent=T.waiting+' '+Math.floor(left/60000)+':'+String(Math.floor(left/1000)%60).padStart(2,'0');" +
                   "const p=await fetch('/pair/poll?cookie=1&id='+encodeURIComponent(j.id)+'&secret='+encodeURIComponent(j.secret),{method:'POST'});const s=(await p.json()).status;" +
                   "if(s==='approved'){location.href='/';return;}if(s==='denied'){st.textContent=T.denied;return;}" +
                   "if(s==='expired'||left<=0){st.textContent=T.expired;return;}setTimeout(tick,2000);};tick();}" +
                   "</script>";
        }
        // Der Hintergrund ist die verkürzte Fassung des Musters aus app.css: diese Seite kommt ohne Stylesheet aus
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1"><title>Stykker LLM</title>
            <style>{{th.CssVariables()}}
            body{margin:0;background:linear-gradient(180deg,var(--bg-top),var(--bg-bottom) 1200px);color:var(--ink);font:15px/1.6 var(--font);display:flex;align-items:center;justify-content:center;min-height:100vh}
            body[data-bg=space],body[data-bg=deep]{background-image:radial-gradient(60% 40% at 80% 10%,rgb(var(--acc-rgb)/.12),transparent 70%),linear-gradient(180deg,var(--bg-top),var(--bg-bottom) 1200px)}
            body[data-bg=cyber]{background-image:radial-gradient(circle,rgb(47 184 214/.3) 1px,transparent 1.4px);background-size:5px 5px}
            body[data-bg=phosphor]{background-image:repeating-linear-gradient(0deg,rgb(var(--good-rgb)/.04) 0 1px,transparent 1px 3px),linear-gradient(180deg,var(--bg-top),var(--bg-bottom) 1200px)}
            .box{background:linear-gradient(180deg,var(--card-top),var(--card-bottom));border:1px solid var(--card-border);border-radius:var(--radius);padding:26px 24px;max-width:420px;margin:16px;box-shadow:inset 0 1px 0 var(--top-line),0 18px 48px rgb(0 0 0/.5)}
            h1{font-size:16px;margin:0 0 14px;letter-spacing:.06em}h2{font-size:15px;margin:0 0 6px;font-weight:600}.muted{color:var(--muted)}.bad{color:var(--bad)}.warn{color:var(--warn)}.small{font-size:13px}
            .qr{background:#fff;border-radius:10px;padding:6px;width:max-content;margin:10px auto}.qr svg{display:block;width:220px;height:220px}.qr:empty{display:none}
            a.btn{display:block;text-align:center;text-decoration:none;background:color-mix(in srgb,var(--bg) 82%,var(--acc));color:var(--ink);border:1px solid color-mix(in srgb,var(--bg) 45%,var(--acc));border-radius:999px;padding:9px 20px;margin-top:6px}
            a.btn:hover{background:color-mix(in srgb,var(--bg) 65%,var(--acc))}
            .code{font-family:var(--font-num);font-size:34px;letter-spacing:.18em;color:var(--acc);margin:6px 0 4px;text-align:center}
            .sep{display:flex;align-items:center;gap:10px;color:var(--muted);font-size:13px;margin:22px 0 4px}.sep::before,.sep::after{content:"";flex:1;border-top:1px solid var(--line)}
            form{display:flex;gap:8px;margin-top:16px}
            input{flex:1;min-width:0;background:color-mix(in srgb,var(--bg) 92%,var(--ink));color:var(--ink);border:1px solid var(--line);border-radius:8px;padding:8px 10px;font:20px var(--font-num);letter-spacing:.2em}
            button{background:color-mix(in srgb,var(--bg) 88%,var(--acc));color:var(--ink);border:1px solid color-mix(in srgb,var(--bg) 45%,var(--acc));border-radius:999px;padding:8px 20px;font:inherit;cursor:pointer}
            button.wide{width:100%}
            </style></head><body data-bg="{{th.Kind}}"><div class="box"><h1>STYKKER <span style="color:var(--acc)">LLM</span></h1>{{body}}</div></body></html>
            """;
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    private static string Json(bool b) => b ? "true" : "false";

    private static Task Json(HttpContext ctx, int status, string body) => Write(ctx, status, "application/json", body);
    private static Task Text(HttpContext ctx, int status, string body) => Write(ctx, status, "text/plain; charset=utf-8", body);

    private static Task Write(HttpContext ctx, int status, string contentType, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        return ctx.Response.WriteAsync(body, Encoding.UTF8);
    }
}
