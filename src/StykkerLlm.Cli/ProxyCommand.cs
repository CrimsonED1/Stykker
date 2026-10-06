using System.Reflection;
using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// Die Bereiche, die der TUI noch fehlten (docs/ui.md): Stykker-Proxy mit Ziel, die anhaltende Hinweiszeile,
// die letzten Anfragen und About. Läuft der Server, kommen Zustand und Aktionen von ihm; sonst aus der eigenen Engine.
// Dieselben Befehle gibt es als "stykker proxy …" für Skripte und als /proxy … in der Oberfläche.
public static class ProxyCommand
{
    // Für "stykker proxy …" / "stykker recent …": eine Sitzung aufmachen, einen Takt holen, den Befehl ausführen
    public static async Task<int> RunAsync(string name, CliArgs a, CancellationToken ct)
    {
        using var s = new Session(a, write: false);
        await s.TickAsync(1, ct).ConfigureAwait(false);
        return name switch
        {
            "proxy" => await ProxyAsync(s, a, ct).ConfigureAwait(false),
            "notice" => await NoticeAsync(s, a, ct).ConfigureAwait(false),
            "recent" => await RecentAsync(s, a, ct).ConfigureAwait(false),
            "about" => About(s.Engine.Paths),
            _ => Commands.Usage,
        };
    }

    // ── /proxy [on|off|lan|serve <server|provider/model>|providers] ──

    public static async Task<int> ProxyAsync(Session s, CliArgs a, CancellationToken ct)
    {
        var what = string.Join(' ', a.Words).ToLowerInvariant();
        bool server = s.UsesServer;
        var px = s.Engine.Proxies;
        var state = server ? s.Server!.Proxy : null;
        string target = state?.TargetName ?? px.TargetName();
        bool running = state?.Running ?? px.Running;
        int port = state?.Port ?? px.Port;
        bool lan = state?.BindLan ?? px.BindLan;

        // Ohne Angabe: die Statuszeile, die auch im Fenster und im Web steht
        if (what.Length == 0)
        {
            Console.Out.WriteLine(running ? Out.Green(Strings.ProxyBar(true, port, target, lan)) : Out.Dim(Strings.ProxyBar(false, port, "", false)));
            Console.Out.WriteLine("  " + Out.Dim(Strings.ProxyClientHint));
            return Commands.Ok;
        }

        // Die eingetragenen Cloud-Anbieter mit ihren Modellen (der Schlüssel steht nie in der Ausgabe)
        if (what == "providers")
            return PrintProviders(state?.Providers ?? ListProviders(px));

        // Alles, was geschrieben wird, macht der Server (eine Engine im System)
        if (!server)
        {
            Out.Error(Strings.ServerNotRunning);
            return Commands.Error;
        }
        var client = s.Remote!;
        ActionResult result;
        if (what is "on" or "off") result = await client.SendAsync("proxy.set", flag: what == "on", ct: ct).ConfigureAwait(false);
        else if (what is "lan" or "local") result = await client.SendAsync("proxy.lan", flag: what == "lan", ct: ct).ConfigureAwait(false);
        else if (what.StartsWith("serve", StringComparison.Ordinal))
        {
            var key = string.Join(' ', a.Words.Skip(1));
            // Ein Cloud-Modell wird als „<Anbieter>/<Modell>" genannt; die Zielkennung holt die Auswahl daraus
            if (key.Contains('/') && state != null)
            {
                var match = state.Choices.FirstOrDefault(c => c.Cloud && c.PublicModel.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (match != null) key = match.Value;
            }
            result = await client.SendAsync("proxy.target", key is "auto" or "off" or "" ? "" : key, ct: ct).ConfigureAwait(false);
        }
        else { Out.Error(Strings.ProxyUsage); return Commands.Usage; }

        Console.Out.WriteLine(result.Ok ? Out.Green(result.Message) : Out.Red(result.Message));
        return result.Ok ? Commands.Ok : Commands.Error;
    }

    // Ohne Server: die Anbieter aus der eigenen Engine (gleiche Liste, gleiche Form)
    private static List<ProxyProviderState> ListProviders(ProxyManager px) =>
        px.ProviderStates().Select(p => new ProxyProviderState
        {
            Name = p.Name,
            Url = p.Url,
            HasKey = p.HasKey,
            Ready = p.Ready,
            Models = p.Models.ToList(),
            Error = p.Error,
        }).ToList();

    private static int PrintProviders(IReadOnlyList<ProxyProviderState> providers)
    {
        var o = Console.Out;
        if (providers.Count == 0) { o.WriteLine(Out.Dim(Strings.ProxyProviderEmpty)); o.WriteLine("  " + Out.Dim(Strings.ProxyProviderHint)); return Commands.Ok; }
        foreach (var p in providers)
        {
            o.WriteLine($"{Out.Bold(p.Name)}  {Out.Dim(p.Url)}");
            o.WriteLine("  " + Out.Dim(p.HasKey ? Strings.ProxyProviderKeyStored : Strings.ProxyProviderKeyMissingShort)
                + "  " + Out.Dim(p.Ready ? Strings.ProxyProviderModels(p.Models.Count) : Strings.ProxyProviderModelsNone));
            if (p.Error.Length > 0) o.WriteLine("  " + Out.Red(p.Error));
            foreach (var m in p.Models) o.WriteLine("    " + m);
        }
        o.WriteLine("  " + Out.Dim(Strings.ProxyProvidersUsage));
        return Commands.Ok;
    }

    // ── /notice  (Text + Logdatei)  ·  /notice dismiss ──

    public static async Task<int> NoticeAsync(Session s, CliArgs a, CancellationToken ct)
    {
        var o = Console.Out;
        bool dismiss = string.Equals(string.Join(' ', a.Words), "dismiss", StringComparison.OrdinalIgnoreCase);
        if (s.UsesServer)
        {
            if (!dismiss) return PrintNotice(s.Server!.Notice);
            var res = await s.Remote!.SendAsync("notice.dismiss", ct: ct).ConfigureAwait(false);
            o.WriteLine(res.Ok ? Out.Green(Strings.BtnDismiss) : Out.Dim(Strings.NoneYet));
            return res.Ok ? Commands.Ok : Commands.Error;
        }
        if (!dismiss) return PrintNotice(s.Engine.CurrentNotice is { } n ? new NoticeState(n.Text, n.Alarm, n.LogFile ?? "") : null);
        if (s.Engine.CurrentNotice == null) { o.WriteLine(Out.Dim(Strings.NoneYet)); return Commands.Ok; }
        s.Engine.DismissNotice();
        o.WriteLine(Out.Green(Strings.BtnDismiss));
        return Commands.Ok;
    }

    private static int PrintNotice(NoticeState? n)
    {
        if (n == null) { Console.Out.WriteLine(Out.Dim(Strings.NoneYet)); return Commands.Ok; }
        Console.Out.WriteLine($"{Out.Yellow(n.Alarm ? Strings.NoticeAlarm : Strings.NoticeHint)}  {n.Text}");
        if (n.LogFile.Length > 0) Console.Out.WriteLine("  " + Out.Dim(Strings.ColLogFile + ": " + n.LogFile));
        Console.Out.WriteLine("  " + Out.Dim(Strings.ProxyNoticeDismiss));
        return Commands.Ok;
    }

    // ── /recent  (letzte Anfragen wie im Fenster und im Web) ──

    public static async Task<int> RecentAsync(Session s, CliArgs a, CancellationToken ct)
    {
        int take = int.TryParse(a.Words.FirstOrDefault(), out var n) && n > 0 ? Math.Min(n, 200) : 20;
        // Serverzustand und eigene Engine liefern beide dieselbe Liste fertiger Anfragen
        return PrintRecent(s.UsesServer ? s.Server!.Servers.Select(x => x.Finished) : s.Engine.Servers.Select(x => x.Finished), take, a.Json);
    }

    private static int PrintRecent(IEnumerable<List<FinishedRequest>> servers, int take, bool json)
    {
        var o = Console.Out;
        var fin = servers.SelectMany(x => x).OrderByDescending(f => f.Seen ?? DateTime.MinValue).Take(take).ToList();
        if (fin.Count == 0) { o.WriteLine(Out.Dim(Strings.NoneYet)); return Commands.Ok; }
        if (json)
        {
            o.WriteLine(System.Text.Json.JsonSerializer.Serialize(fin.Select(f => new
            {
                f.Server, f.Model, f.Task, f.Seen, f.PromptTokens, f.PromptTps, f.GenTps, f.GenTokens, f.Seconds, status = f.Status.ToString(),
            }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return Commands.Ok;
        }
        Out.Table(new[] { Strings.ColServer, Strings.ColTime, Strings.ColTask, Strings.ColReqPrompt, Strings.ColReqGeneration, Strings.ColReqTokens, Strings.ColReqTime, Strings.ColStatus },
            fin.Select(f => (IReadOnlyList<string>)new[]
            {
                Out.Cut(f.Server, 20), f.Seen?.ToString("HH:mm:ss") ?? Strings.FromLog, Strings.TaskName(f.Task),
                $"{f.PromptTps:0} t/s", $"{f.GenTps:0.0} t/s", f.GenTokens.ToString(),
                f.Seconds > 0 ? $"{f.Seconds:0} s" : "–", Out.Cut(f.Status.ToString().ToLowerInvariant(), 10),
            }), o);
        return Commands.Ok;
    }

    // ── /about ──

    public static int About(AppPaths paths)
    {
        var version = typeof(ProxyCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        Console.Out.WriteLine($"{Out.Bold(Strings.AppName)}  {Out.Dim(version)}  {Out.Dim(Strings.AboutLicense)}");
        Console.Out.WriteLine($"  {Out.Dim(Strings.AboutDataDir)}  {paths.Root}");
        Console.Out.WriteLine($"  {Out.Dim(Strings.AboutServer)}   stykker web");
        return Commands.Ok;
    }
}