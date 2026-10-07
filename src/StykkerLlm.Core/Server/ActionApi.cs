using System.Text.Json;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Core;

// ── Aktionen an den Server (S3) ──
// Fenster, TUI, Telefon und Web schicken dieselben Aktionen an dieselbe Stelle: {"action":"start","arg":"…"}.
// Der Server führt sie gegen seine Engine aus. Rückfragen laufen über IUserPrompt; wer ohne Rückfrage handelt
// (API, Telefon), benutzt RemotePrompt: Rückfragen werden bejaht, Geheimnisse kommen im Feld "secret" mit.
public sealed class ActionRequest
{
    public string Action { get; set; } = "";
    public string? Arg { get; set; }        // Server-Key, Profil-ID, Modell-ID, Dateiname …
    public string? Arg2 { get; set; }       // zweiter Wert (z. B. Modell beim Entladen, Ziel beim Proxy)
    public string? Name { get; set; }       // Name für ein neues Profil
    public bool Flag { get; set; }          // Schalter (an/aus, leere Warteschlange, Code erneuern)
    public int Number { get; set; }         // Zahl (Wiederholungen, Intervall, Anzahl neuer Modelle)
    public List<string> Ids { get; set; } = new();
    public string? Secret { get; set; }     // Geheimnis für den Start, falls das Profil eines braucht
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
    public int GetInt(string key, int def = 0) => int.TryParse(Get(key), out var i) ? i : def;
    public bool GetBool(string key) => Get(key) is "1" or "true" or "yes" or "on";
    public int[] GetInts(string key) => (Get(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).ToArray();

    public static ActionRequest Parse(string json)
    {
        var req = new ActionRequest();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            req.Action = J.Str(r, "action") ?? "";
            req.Arg = J.Str(r, "arg");
            req.Arg2 = J.Str(r, "arg2");
            req.Name = J.Str(r, "name");
            req.Flag = J.Bool(r, "flag");
            req.Number = J.Int(r, "number");
            req.Secret = J.Str(r, "secret");
            req.Ids = J.StrList(r, "ids");
            if (r.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object)
                foreach (var p in v.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) req.Values[p.Name] = p.Value.GetString()!;
                    else if (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) req.Values[p.Name] = p.Value.ToString();
        }
        catch { /* dann bleibt die Anfrage leer und wird abgelehnt */ }
        return req;
    }
}

public sealed record ActionResult(bool Ok, string Message = "", string? Data = null)
{
    public static ActionResult Fail(string message) => new(false, message);
    public static ActionResult Done(string message = "ok") => new(true, message);
}

// Was die Aktionen brauchen. Der Server baut das einmal; Fenster und TUI brauchen es nicht (sie schicken nur Aktionen).
public sealed class ActionContext
{
    public required MonitorEngine Engine { get; init; }
    public required LaunchCoordinator Launcher { get; init; }
    public AccessControl? Access { get; init; }
    public EvalQueue? Queue { get; init; }
    public BenchmarkService? Benchmarks { get; init; }
    // Gekoppelte Nodes (dieser Server als Hub)
    public NodeRegistry? Nodes { get; init; }
    // Model-Hosts, die sich einwählen (docs/plan-hosts-gateway.md)
    public HostHub? Hosts { get; init; }
    // Server beenden (Web-Kopf, „stykker stop --server“)
    public Func<Task>? Shutdown { get; init; }
    public int ServerPort { get; init; }
}

// Rückfragen ohne Bildschirm: bejaht, nimmt ein mitgeschicktes Geheimnis, sammelt Meldungen
public sealed class RemotePrompt : IUserPrompt
{
    private readonly string? _secret;
    public RemotePrompt(string? secret = null) => _secret = secret;
    public List<string> Messages { get; } = new();

    public Task<string?> AskSecretAsync(string title, string text) => Task.FromResult(_secret);
    public Task<bool> ConfirmAsync(string title, string text, bool warning = false)
    {
        Messages.Add(text);
        return Task.FromResult(true);
    }
    public Task InformAsync(string title, string text, bool warning = false)
    {
        Messages.Add(text);
        return Task.CompletedTask;
    }
}

public static class ActionApi
{
    // Rollen: ein Viewer-Gerät darf alles lesen, aber nichts verändern. Die Liste ist bewusst leer – wer etwas
    // ergänzen will, schreibt es hier hinein; alles andere (auch unbekannte Aktionen) bleibt Admin-only.
    private static readonly HashSet<string> ViewerMayRun = new(StringComparer.Ordinal);

    public static bool ViewerAllowed(string? action) => action != null && ViewerMayRun.Contains(action);

    public static bool HubForbidden(string? action) =>
        action is "code.rotate" or "device.remove" or "device.role" or "remote.set" or "pair.approve" or "pair.deny" or "shutdown" or "bugreport.create"
        || (action?.StartsWith("node.", StringComparison.Ordinal) ?? false)
        || (action?.StartsWith("host.", StringComparison.Ordinal) ?? false);

    /// <param name="role">Rolle des Aufrufers (AccessRole). null = Admin (Schlüssel aus dem Datenordner, also Fenster/TUI).</param>
    public static async Task<ActionResult> ExecuteAsync(ActionRequest req, ActionContext ctx, IUserPrompt prompt, CancellationToken ct = default, string? role = null)
    {
        var e = ctx.Engine;
        if (!AccessRole.CanWrite(role) && !ViewerAllowed(req.Action)) return ActionResult.Fail(Strings.ViewerOnly);
        // Ein Hub steuert Server, Tests und Proxy des Nodes, aber nicht dessen Zugang (Code, Geräte, Home/VPN):
        // sonst könnte ein gekoppelter Rechner sich selbst unentfernbar machen oder andere Geräte aussperren
        if (AccessRole.Normalize(role) == AccessRole.Hub && HubForbidden(req.Action)) return ActionResult.Fail(Strings.NodeHubNotAllowed);
        if (e.ReadOnly) return ActionResult.Fail(Strings.ReadOnlyEngine);
        ActionResult Ok(string msg = "ok", string? data = null) => new(true, msg, data);

        try
        {
            switch (req.Action)
            {
                // ── Server starten und stoppen ──
                case "start":
                {
                    var profile = FindProfile(e.Library, req.Arg);
                    if (profile == null) return ActionResult.Fail($"profile '{req.Arg}' not found");
                    bool ok = await ctx.Launcher.StartProfileAsync(profile);
                    // Bei einem Fehlschlag das **Programm** nennen, nicht die Profil-ID: über die API kommt als
                    // Argument die ID, und die half beim Suchen nicht weiter ("… could not be started: 3f2a…").
                    return ok ? Ok(profile.Name, profile.Id.ToString("N")) : ActionResult.Fail(Strings.StartFailed(profile.Program));
                }
                case "start-history":
                {
                    var h = e.Library.History.FirstOrDefault(x => x.Key == req.Arg || x.Name == req.Arg);
                    if (h == null) return ActionResult.Fail($"history entry '{req.Arg}' not found");
                    bool ok = await ctx.Launcher.StartHistoryAsync(h);
                    return ok ? Ok(h.Name) : ActionResult.Fail(Strings.StartFailed(h.Program));
                }
                case "stop":
                {
                    var s = FindServer(e, req.Arg);
                    if (s == null) return ActionResult.Fail($"server '{req.Arg}' not found");
                    await ctx.Launcher.StopServerAsync(s);
                    return Ok(s.Name);
                }
                case "unload":
                {
                    var s = FindServer(e, req.Arg);
                    if (s == null || string.IsNullOrEmpty(req.Arg2)) return ActionResult.Fail("server or model missing");
                    await ctx.Launcher.UnloadModelAsync(s, req.Arg2!);
                    return Ok(req.Arg2!);
                }
                case "launch.dismiss":
                {
                    // Karte "starting …" wegklicken (der Prozess läuft weiter, falls er noch lebt)
                    if (!Guid.TryParse(req.Arg, out var dismissId)) return ActionResult.Fail("launch id missing");
                    if (e.Registry.Launches.All(x => x.Id != dismissId)) return ActionResult.Fail("launch not found");
                    e.Registry.DismissLaunch(dismissId);
                    return Ok();
                }
                case "launch.stop":
                {
                    // Der Prozess eines fehlgeschlagenen Starts (z. B. nach der Zeitüberschreitung) beenden
                    if (!Guid.TryParse(req.Arg, out var launchId)) return ActionResult.Fail("launch id missing");
                    var l = e.Registry.Launches.FirstOrDefault(x => x.Id == launchId);
                    if (l == null) return ActionResult.Fail("launch not found");
                    await ctx.Launcher.StopLaunchedAsync(l);
                    return Ok();
                }
                case "freevram":
                    await ctx.Launcher.FreeVramAsync();
                    return Ok();

                // ── Bibliothek ──
                case "save":
                {
                    var s = FindServer(e, req.Arg);
                    if (s == null) return ActionResult.Fail($"server '{req.Arg}' not found");
                    if (!s.Info.CanSave) return ActionResult.Fail(Strings.SaveBlockText(s.Info.SaveBlock));
                    e.Library.AddProfile(s.Info, req.Name, DateTime.Now);
                    e.Library.Save();
                    return Ok(req.Name ?? s.Name);
                }
                case "profile.rename":
                case "profile.note":
                {
                    if (!TryProfile(e.Library, req.Arg, out var p)) return ActionResult.Fail("profile not found");
                    if (req.Action == "profile.rename") e.Library.RenameProfile(p!.Id, req.Name ?? "");
                    else e.Library.SetNote(p!.Id, req.Name);
                    e.Library.Save();
                    return Ok();
                }
                case "profile.args":
                {
                    if (!TryProfile(e.Library, req.Arg, out var p)) return ActionResult.Fail("profile not found");
                    e.Library.UpdateArgs(p!.Id, req.Get("args") is { } a ? CmdLine.Split(a) : Array.Empty<string>());
                    e.Library.Save();
                    return Ok();
                }
                // Automatischer Neustart nach einem Absturz: Schalter und Grenze je Profil
                case "profile.restart":
                {
                    if (!TryProfile(e.Library, req.Arg, out var p)) return ActionResult.Fail("profile not found");
                    p!.RestartOnCrash = req.Flag;
                    p.MaxRestarts = Math.Clamp(req.Number, 0, 10);   // 0 = Vorgabe der Policy (3)
                    e.Library.Save();
                    return Ok();
                }
                // Leerlauf-Entladen: Minuten ohne Anfrage, danach stoppt der Server
                case "profile.idleunload":
                {
                    if (!TryProfile(e.Library, req.Arg, out var p)) return ActionResult.Fail("profile not found");
                    p!.UnloadAfterIdleMin = Math.Clamp(req.Number, 0, 720);   // 0 = aus
                    e.Library.Save();
                    return Ok(p.UnloadAfterIdleMin == 0 ? Strings.IdleUnloadOff : Strings.IdleUnloadSet(p.UnloadAfterIdleMin));
                }
                case "profile.remove":
                {
                    if (!TryProfile(e.Library, req.Arg, out var p)) return ActionResult.Fail("profile not found");
                    e.Library.RemoveProfile(p!.Id);
                    e.Library.Save();
                    return Ok();
                }
                case "history.forget":
                    e.Library.ForgetHistory(req.Arg ?? "");
                    e.Library.Save();
                    return Ok();
                case "profile.add-history":
                {
                    // Aus dem Verlauf merken (das Fenster sendet das, wenn der Server die Bibliothek hält)
                    var h = e.Library.FindHistory(req.Arg ?? "");
                    if (h == null) return ActionResult.Fail("history entry not found");
                    if (h.Program.Length == 0) return ActionResult.Fail(Strings.SaveBlockNoCommand);
                    e.Library.AddProfile(h, req.Name, DateTime.Now);
                    e.Library.Save();
                    return Ok();
                }
                case "manual.add":
                {
                    var m = new ManualServer { Name = req.Name ?? "", Url = req.Arg ?? "", Log = req.Arg2, Kind = req.Get("kind") };
                    if (string.IsNullOrWhiteSpace(m.Url)) return ActionResult.Fail("url missing");
                    e.AddManualServer(m);
                    return Ok(m.Url);
                }
                case "manual.remove":
                {
                    var s = FindServer(e, req.Arg);
                    if (s == null) return ActionResult.Fail("server not found");
                    e.RemoveManualServer(s);
                    return Ok();
                }

                // ── Proxy ──
                case "proxy.toggle":
                {
                    bool ok = e.Proxies.Toggle(out var err);
                    return ok ? Ok(e.Settings.ProxyEnabled ? Strings.ProxyOn(e.Proxies.Port) : Strings.ProxyOff)
                              : ActionResult.Fail(err ?? Strings.ProxyFailed);
                }
                case "proxy.set":
                {
                    // Ausdrücklich ein oder aus (die TUI kennt keinen zweiten Knopf zum Umkippen)
                    if (e.Proxies.Running == req.Flag) return Ok(req.Flag ? Strings.ProxyOn(e.Proxies.Port) : Strings.ProxyOff);
                    bool ok = e.Proxies.Toggle(out var err);
                    return ok || !req.Flag ? Ok(e.Settings.ProxyEnabled ? Strings.ProxyOn(e.Proxies.Port) : Strings.ProxyOff)
                                           : ActionResult.Fail(err ?? Strings.ProxyFailed);
                }
                case "proxy.lan":
                    e.Proxies.SetBindLan(req.Flag);
                    return Ok(req.Flag ? Strings.ProxyLanOn : Strings.ProxyLanOff);
                case "proxy.target":
                    e.Proxies.SetChoiceValue(req.Arg ?? "");
                    e.Settings.Save();
                    return Ok(e.Proxies.TargetName());
                case "proxy.port":
                {
                    int port = req.GetInt("port", e.Proxies.Port);
                    if (port is < 1024 or > 65535) return ActionResult.Fail("port must be 1024…65535");
                    e.Settings.ProxyPort = port;
                    e.Settings.Save();
                    e.Proxies.Restart();
                    return Ok(port.ToString(Strings.Inv));
                }

                // ── Andere Rechner und Cloud-Anbieter ──
                case "remote.add":
                    e.Proxies.AddRemote(req.Get("name") ?? "", req.Get("url") ?? "");
                    return Ok(req.Get("name") ?? req.Get("url") ?? "");
                case "remote.remove":
                    e.Proxies.RemoveRemote(req.Arg ?? "");
                    return Ok();
                // ── Cloud-Anbieter (Nr. 46): Opt-in je Anbieter, der Schlüssel kommt im Feld „secret" und bleibt dort ──
                case "provider.add":
                {
                    e.Proxies.AddProvider(req.Get("name") ?? "", req.Get("url") ?? "", req.Secret ?? "", out var perr);
                    return perr == null ? Ok(Strings.ProxyProviderAdded) : ActionResult.Fail(perr);
                }
                case "provider.key":
                {
                    e.Proxies.SetProviderKey(req.Arg ?? "", req.Secret ?? "", out var kerr);
                    return kerr == null ? Ok(Strings.ProxyProviderKeySet) : ActionResult.Fail(kerr);
                }
                case "provider.remove":
                {
                    e.Proxies.RemoveProvider(req.Arg ?? "");
                    return Ok(Strings.ProxyProviderRemoved);
                }

                // ── Aufnahmen ──
                case "record.start":
                {
                    if (req.Arg is "all" or null or "")
                    {
                        var all = await e.StartRecordingAllAsync();
                        return all == null ? ActionResult.Fail(Strings.NothingToRecord) : Ok(all.Id);
                    }
                    var rec = FindServer(e, req.Arg);
                    if (rec == null) return ActionResult.Fail($"server '{req.Arg}' not found");
                    var session = e.StartRecording(rec, out var problem);
                    return session == null ? ActionResult.Fail(problem ?? Strings.RecordingFailed) : Ok(session.Id);
                }
                case "record.stop":
                {
                    var sessions = e.Recorder.Sessions.ToList();
                    if (req.Arg is "all" or null or "")
                    {
                        int n = 0;
                        foreach (var one in sessions) { await e.StopRecordingAsync(one, quiet: true); n++; }
                        return Ok(n.ToString(Strings.Inv));
                    }
                    var target = sessions.FirstOrDefault(x => x.Target == req.Arg || x.Id == req.Arg);
                    if (target == null) return ActionResult.Fail("no recording for this server");
                    var sum = await e.StopRecordingAsync(target);
                    return Ok(sum?.Id ?? "");
                }
                case "record.delete":
                    return DeleteRecording(e.Paths, req.Arg ?? "");

                // ── Benchmark ──
                case "bench.cancel":
                    ctx.Benchmarks?.Cancel();
                    return Ok();
                case "bench.run":
                {
                    if (ctx.Benchmarks == null) return ActionResult.Fail("benchmarks are not available here");
                    var options = new BenchOptions
                    {
                        Chat = req.GetBool("chat"),
                        Tool = req.GetBool("tool"),
                        Parallel = req.GetBool("parallel"),
                        Repeats = Math.Clamp(req.GetInt("repeats", 1), 1, 10),
                        GenTokens = Math.Clamp(req.GetInt("genTokens", 128), 16, 4096),
                        ContextSizes = req.GetInts("contextSizes"),
                        ContextPerSlot = Math.Clamp(req.GetInt("contextPerSlot", 0), 0, 1 << 20),
                        Slots = Math.Clamp(req.GetInt("slots", 1), 1, 64),
                        Seed = req.GetInt("seed", 42),
                    };
                    if (!options.Chat && options.ContextSizes.Length == 0 && !options.Tool && !options.Parallel)
                        return ActionResult.Fail(Strings.BenchNothingSelected);
                    var result = await ctx.Benchmarks.RunAsync(e, ctx.Launcher, req.Arg ?? "", options, ct);
                    return result == null ? ActionResult.Fail(ctx.Benchmarks.Status) : Ok(result.Title, result.Id.ToString("N"));
                }

                // ── Modelltests (Eval) ──
                case "eval.enqueue":
                {
                    var q = ctx.Queue;
                    if (q == null) return ActionResult.Fail("model tests are not available here");
                    if (req.Flag) q.AllowCode = true;
                    var suites = (req.Get("suites") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var models = q.Models.Where(m => req.Ids.Count == 0 || req.Ids.Contains(m.Id)).Where(m => m.Enabled).ToList();
                    if (models.Count == 0) return ActionResult.Fail(Strings.EvalNoModels);
                    if (suites.Length == 0) return ActionResult.Fail(Strings.EvalPickSuite);
                    q.Enqueue(models, suites, Math.Clamp(req.Number > 0 ? req.Number : 1, 1, 20));
                    q.Start();
                    return Ok(models.Count.ToString(Strings.Inv));
                }
                case "eval.distribute":
                {
                    // Verteilte Tests (N3): Ids = Modelldateien; jede geht an den PC, auf dem sie liegt und der am
                    // wenigsten zu tun hat (NodeScheduler). Dieser PC reiht direkt ein, Nodes über ihre eigene Warteschlange.
                    var q = ctx.Queue;
                    if (q == null) return ActionResult.Fail("model tests are not available here");
                    var suiteList = (req.Get("suites") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (suiteList.Length == 0) return ActionResult.Fail(Strings.EvalPickSuite);
                    int repeat = Math.Clamp(req.Number > 0 ? req.Number : 1, 1, 20);
                    var selfState = StateSnapshot.Parse(StateJson.WriteText(e, null, q, ctx.ServerPort, DateTimeOffset.Now, withHistory: false));
                    var self = NodeStateJson.Self(selfState, NetAddr.Url("127.0.0.1", ctx.ServerPort));
                    var plan = NodeScheduler.Assign(req.Ids, self, NodeStateJson.FromRegistry(ctx.Nodes).List);
                    if (plan.Count == 0) return ActionResult.Fail(Strings.EvalNoModels);
                    var notes = new List<string>();
                    foreach (var group in plan.GroupBy(x => x.NodeId))
                    {
                        if (group.Key == self.Id)
                        {
                            if (req.Flag) q.AllowCode = true;
                            var mine = q.Models.Where(m => group.Any(x => x.ModelId == m.Id)).ToList();
                            q.Enqueue(mine, suiteList, repeat);
                            q.Start();
                            notes.Add($"{Strings.NodeThisPc}: {mine.Count}");
                            continue;
                        }
                        var inner = new ActionRequest { Action = "eval.enqueue", Number = repeat, Flag = req.Flag, Ids = group.Select(x => x.ModelId).ToList() };
                        inner.Values["suites"] = string.Join(',', suiteList);
                        var r = ctx.Nodes == null ? ActionResult.Fail(Strings.NodeUnknown) : await ctx.Nodes.SendAsync(group.Key, inner, ct);
                        notes.Add($"{group.First().NodeName}: {(r.Ok ? group.Count().ToString(Strings.Inv) : r.Message)}");
                    }
                    return Ok(string.Join(" · ", notes), System.Text.Json.JsonSerializer.Serialize(plan));
                }
                case "eval.try":
                {
                    var q = ctx.Queue;
                    if (q == null) return ActionResult.Fail("model tests are not available here");
                    var model = q.Models.FirstOrDefault(m => m.Id == req.Arg);
                    if (model == null) return ActionResult.Fail("model not found");
                    var suite = EvalSuites.BuiltInNames.Contains(req.Arg2 ?? "", StringComparer.OrdinalIgnoreCase) ? EvalSuites.BuiltIn(req.Arg2!) : EvalSuites.Load(req.Arg2 ?? "");
                    if (suite == null) return ActionResult.Fail($"suite '{req.Arg2}' not found");
                    q.EnqueueInline(model, suite);
                    q.Start();
                    return Ok(suite.Name);
                }
                case "eval.stop":
                    ctx.Queue?.Stop();
                    return Ok();
                case "eval.job.remove":
                    if (!Guid.TryParse(req.Arg, out var jobId)) return ActionResult.Fail("job id missing");
                    ctx.Queue?.Remove(jobId);
                    return Ok();
                case "eval.clear":
                    ctx.Queue?.ClearFinished();
                    return Ok();
                case "eval.discover":
                {
                    var q = ctx.Queue;
                    if (q == null) return ActionResult.Fail("model tests are not available here");
                    int found = q.Discover(e.Library, q.Settings.ModelRoots.ToArray(), q.Settings.LlamaServer);
                    q.SaveModels();
                    return Ok(found.ToString(Strings.Inv), found.ToString(Strings.Inv));
                }
                case "eval.allowcode":
                    if (ctx.Queue == null) return ActionResult.Fail("not available");
                    ctx.Queue.AllowCode = req.Flag;
                    return Ok(ctx.Queue.AllowCode ? Strings.On : Strings.Off);
                case "eval.model.remove":
                {
                    var q = ctx.Queue;
                    if (q == null) return ActionResult.Fail("not available");
                    q.Models.RemoveAll(m => m.Id == req.Arg);
                    q.SaveModels();
                    return Ok();
                }
                case "eval.model.upsert":
                {
                    var q = ctx.Queue;
                    if (q == null) return ActionResult.Fail("not available");
                    var m = q.Models.FirstOrDefault(x => x.Id == req.Arg);
                    if (m == null)
                    {
                        m = new EvalModelDef();
                        q.Models.Add(m);
                    }
                    if (req.Name != null) m.Name = req.Name;
                    if (req.Get("exe") is { } exe) m.Exe = exe;
                    if (req.Get("args") is { } args) m.Args = args;
                    if (req.Get("modelFile") is { } file) m.ModelFile = file;
                    if (req.Get("port") is { } port && int.TryParse(port, out var pn)) m.Port = pn;
                    if (req.Get("enabled") is { } en) m.Enabled = en is "1" or "true" or "yes" or "on";
                    q.SaveModels();
                    return Ok(m.Id);
                }

                // ── Einstellungen ──
                case "theme.set":
                    e.Settings.Theme = req.Arg ?? e.Settings.Theme;
                    e.Settings.Save();
                    return Ok(e.Settings.Theme);
                case "settings.set":
                {
                    if (req.Get("intervalMs") is { } iv && int.TryParse(iv, out var ivn)) e.Settings.IntervalMs = Math.Clamp(ivn, 250, 60_000);
                    if (req.Get("maxRecordings") is { } mr && int.TryParse(mr, out var mrn)) e.Settings.MaxRecordings = Math.Clamp(mrn, 0, 10_000);
                    if (req.Get("maxRecordingsMb") is { } mm && int.TryParse(mm, out var mmn)) e.Settings.MaxRecordingsMb = Math.Clamp(mmn, 10, 100_000);
                    if (req.Get("maxRecordingMb") is { } mb && int.TryParse(mb, out var mbn)) e.Settings.MaxRecordingMb = Math.Clamp(mbn, 1, 100_000);
                    if (req.Get("maxLogFiles") is { } lf && int.TryParse(lf, out var lfn)) e.Settings.MaxLogFiles = Math.Clamp(lfn, 1, 1000);
                    if (req.Get("maxLogsMb") is { } lm && int.TryParse(lm, out var lmn)) e.Settings.MaxLogsMb = Math.Clamp(lmn, 1, 100_000);
                    if (req.Get("maxCsvMb") is { } cm && int.TryParse(cm, out var cmn)) e.Settings.MaxCsvMb = Math.Clamp(cmn, 1, 100_000);
                    if (req.Get("startTimeoutMin") is { } st && int.TryParse(st, out var stn)) e.Settings.StartTimeoutMin = Math.Clamp(stn, 1, 120);
                    if (req.Get("gpuTopCount") is { } gt && int.TryParse(gt, out var gtn)) e.Settings.GpuTopCount = Math.Clamp(gtn, 0, 32);
                    // 0 schaltet die Regressions-Meldung ab, 100 meldet jeden Rückgang
                    if (req.Get("benchRegressionPct") is { } br && int.TryParse(br, out var brn)) e.Settings.BenchRegressionPct = Math.Clamp(brn, 0, 100);
                    if (req.Get("keepServer") is { } ks && bool.TryParse(ks, out var ksb)) e.Settings.KeepServerRunning = ksb;
                    e.Settings.Save();
                    return Ok();
                }

                // ── Model-Hosts ──
                case "host.code":
                    if (ctx.Hosts == null) return ActionResult.Fail("not available");
                    ctx.Hosts.Pairing.Rotate();
                    return Ok();
                case "host.remove":
                    if (ctx.Hosts == null) return ActionResult.Fail("not available");
                    return await ctx.Hosts.RemoveAsync(req.Arg ?? "").ConfigureAwait(false) ? Ok() : ActionResult.Fail(Strings.HostNotFound);

                // ── Fehlerbericht: Zip im Datenordner, Daten = Pfad und GitHub-Adresse (durch Zeilenumbruch getrennt) ──
                case "bugreport.create":
                {
                    if (string.IsNullOrWhiteSpace(req.Arg)) return ActionResult.Fail(Strings.BugReportEmpty);
                    var state = StateJson.WriteText(e, ctx.Access, ctx.Queue, ctx.ServerPort, DateTimeOffset.Now, withHistory: false, withCode: false, nodes: ctx.Nodes);
                    var report = BugReport.Create(e.Paths, req.Arg, state, DateTime.Now, gpu: e.Gpu?.Name);
                    AppLog.Write($"bug report: {report.ZipPath}");
                    return Ok(report.Summary, report.ZipPath + "\n" + report.IssueUrl + "\n" + string.Join("|", report.Entries));
                }

                // ── Heimnetz und Geräte ──
                case "remote.set":
                {
                    if (ctx.Access == null) return ActionResult.Fail("not available");
                    ctx.Access.SetRemote(req.Flag);
                    return Ok(ctx.Access.RemoteEnabled ? Strings.RemoteOn : Strings.RemoteOff);
                }
                case "code.rotate":
                {
                    if (ctx.Access == null) return ActionResult.Fail("not available");
                    var code = ctx.Access.RotateCode();
                    return Ok(code, NetInfo.PairUrl(ctx.ServerPort, code));
                }
                case "pair.approve":
                {
                    // Die Ziffern, die das neue Gerät zeigt (Netflix-Weg); Arg2 = gewünschte Rolle für einen Browser
                    if (ctx.Access == null) return ActionResult.Fail("not available");
                    var r = ctx.Access.Approve(req.Arg, string.IsNullOrWhiteSpace(req.Arg2) ? null : req.Arg2!.Trim().ToLowerInvariant());
                    return r == null ? ActionResult.Fail(Strings.PairApproveUnknown) : Ok(Strings.PairApproved(r.Name));
                }
                case "pair.deny":
                {
                    if (ctx.Access == null) return ActionResult.Fail("not available");
                    return ctx.Access.Deny(req.Arg) ? Ok() : ActionResult.Fail(Strings.PairApproveUnknown);
                }
                // ── Nodes (docs/nodes.md) ──
                case "node.search":
                {
                    var found = await NodeDiscovery.SearchAsync(TimeSpan.FromSeconds(2), ct);
                    var paired = ctx.Nodes?.Nodes.Select(n => n.Entry.Url).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
                    var list = found.Where(f => !f.Self).Select(f => new { f.Name, f.Url, f.Remote, paired = paired.Contains(f.Url) }).ToList();
                    return Ok(list.Count == 0 ? Strings.NodeSearchNone : Strings.NodeFound(list.Count), System.Text.Json.JsonSerializer.Serialize(list));
                }
                case "node.pair":
                {
                    // Arg = Adresse, Arg2 = Code des Nodes (leer: Netflix-Weg, der Hub zeigt einen Code)
                    if (ctx.Nodes == null) return ActionResult.Fail("not available");
                    return string.IsNullOrWhiteSpace(req.Arg2)
                        ? await ctx.Nodes.StartPairAsync(req.Arg, ct)
                        : await ctx.Nodes.PairWithCodeAsync(req.Arg, req.Arg2, ct);
                }
                case "node.pair.cancel":
                    ctx.Nodes?.CancelPairing();
                    return Ok();
                case "node.remove":
                    if (ctx.Nodes == null) return ActionResult.Fail("not available");
                    return await ctx.Nodes.RemoveAsync(req.Arg, ct) ? Ok() : ActionResult.Fail(Strings.NodeUnknown);
                case "node.rename":
                    if (ctx.Nodes == null) return ActionResult.Fail("not available");
                    return ctx.Nodes.Rename(req.Arg, req.Arg2) ? Ok() : ActionResult.Fail(Strings.NodeUnknown);
                case "node.do":
                {
                    // Eine Aktion auf dem Node: Arg = Node, Name = Aktion, Arg2/Flag/Number/Ids/Values werden durchgereicht
                    if (ctx.Nodes == null) return ActionResult.Fail("not available");
                    if (string.IsNullOrWhiteSpace(req.Name) || HubForbidden(req.Name)) return ActionResult.Fail(Strings.NodeHubNotAllowed);
                    var inner = new ActionRequest
                    {
                        Action = req.Name!, Arg = req.Arg2, Arg2 = req.Values.TryGetValue("arg2", out var a2) ? a2 : null,
                        Flag = req.Flag, Number = req.Number, Ids = req.Ids, Secret = req.Secret,
                    };
                    foreach (var kv in req.Values.Where(kv => kv.Key != "arg2")) inner.Values[kv.Key] = kv.Value;
                    return await ctx.Nodes.SendAsync(req.Arg, inner, ct);
                }

                case "device.remove":
                {
                    if (ctx.Access == null) return ActionResult.Fail("not available");
                    return ctx.Access.RemoveDevice(req.Arg ?? "") ? Ok() : ActionResult.Fail("device not found");
                }
                case "device.role":
                {
                    if (ctx.Access == null) return ActionResult.Fail("not available");
                    var want = (req.Arg2 ?? "").Trim().ToLowerInvariant();
                    if (!AccessRole.IsValid(want)) return ActionResult.Fail(Strings.RoleUnknown(want));
                    return ctx.Access.SetRole(req.Arg ?? "", want)
                        ? Ok(want == AccessRole.Viewer ? Strings.RoleViewer : Strings.RoleAdmin)
                        : ActionResult.Fail("device not found");
                }

                // ── Server ──
                case "shutdown":
                    if (ctx.Shutdown == null) return ActionResult.Fail("not available");
                    _ = Task.Run(async () => { await Task.Delay(200); await ctx.Shutdown(); });
                    return Ok(Strings.ServerStopping);
                case "notice":
                    StateJson.Notice(req.Arg ?? "");
                    return Ok();
                case "notice.dismiss":
                    // Der anhaltende Hinweis verschwindet erst, wenn ihn jemand wegklickt (alle Oberflächen)
                    if (e.CurrentNotice == null) return ActionResult.Fail("no notice");
                    e.DismissNotice();
                    return Ok();
                default:
                    return ActionResult.Fail($"unknown action '{req.Action}'");
            }
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(ex.Message);
        }
        finally
        {
            if (prompt is RemotePrompt rp && rp.Messages.Count > 0) StateJson.Notice(rp.Messages[^1]);
        }
    }

    private static ServerWatcher? FindServer(MonitorEngine e, string? key) =>
        string.IsNullOrEmpty(key) ? null : e.Servers.FirstOrDefault(s => s.Key == key || s.Name == key || NetAddr.Key(s.Info.Host, s.Info.Port) == key);

    private static bool TryProfile(Library lib, string? idOrName, out Profile? profile)
    {
        profile = null;
        if (string.IsNullOrEmpty(idOrName)) return false;
        profile = lib.Profiles.FirstOrDefault(p => p.Id.ToString("N") == idOrName || p.Id.ToString() == idOrName)
            ?? lib.Profiles.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        return profile != null;
    }

    private static Profile? FindProfile(Library lib, string? idOrName) =>
        TryProfile(lib, idOrName, out var p) ? p : null;

    // Aufnahmen löschen: nur Dateien im Aufnahmeordner (eine Telefon-Anfrage darf nichts außerhalb löschen)
    private static ActionResult DeleteRecording(AppPaths paths, string file)
    {
        try
        {
            var root = Path.GetFullPath(paths.RecordingsDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return ActionResult.Fail("not a recording of this monitor");
            var summary = RecordingStore.SummaryPath(full);
            var existed = File.Exists(full) || File.Exists(summary);
            Try(() => File.Delete(full));
            Try(() => File.Delete(summary));
            Try(() => File.Delete(Path.ChangeExtension(full, ".txt")));
            return existed ? ActionResult.Done(Strings.RecordingDeleted) : ActionResult.Fail("file not found");
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(ex.Message);
        }

        static void Try(Action a) { try { a(); } catch { } }
    }
}
