namespace StykkerLlm.Core;

// Start und Stopp von Servern als Ablauf mit Rückfragen (IUserPrompt): Prüfungen, Warnungen, Bestätigung der Kommandozeile,
// Geheimnisse erfragen, Plan bauen, Prozess starten bzw. nach Bestätigung beenden. Ohne Oberfläche, von der Oberfläche genutzt.
public sealed class LaunchCoordinator
{
    private readonly MonitorEngine _engine;
    private readonly IUserPrompt _prompt;

    public LaunchCoordinator(MonitorEngine engine, IUserPrompt prompt)
    {
        _engine = engine;
        _prompt = prompt;
    }

    public ServerWatcher? RunningFor(Profile p) =>
        _engine.Servers.FirstOrDefault(s => s.Info.Program != null && Library.MakeKey(s.Info.Program, s.Info.Args) == p.Key);

    public LaunchedServer? StartingFor(Profile p) =>
        _engine.Registry.Launches.FirstOrDefault(l => l.ProfileId == p.Id && l.State == LaunchState.Starting);

    // ── Starten ──

    public Task<bool> StartProfileAsync(Profile p)
    {
        var hist = _engine.Library.FindHistory(p.Key);
        return StartSpecAsync(new LaunchSpec(p.Name, p.Program, p.Args, p.WorkingDir, p.Id, hist is { MaxVramGb: > 0 } ? hist.MaxVramGb : null, p.Env));
    }

    public Task<bool> StartHistoryAsync(HistoryEntry h)
    {
        var name = h.Name.Length > 0 ? h.Name : h.ModelPath != null ? ServerInfo.ModelName(h.ModelPath) : "server";
        return StartSpecAsync(new LaunchSpec(name, h.Program, h.Args, h.WorkingDir, null, h.MaxVramGb > 0 ? h.MaxVramGb : null, h.Env));
    }

    // Prüfen (Fehler/Warnungen), Kommandozeile bestätigen lassen (beim ersten Start und nach jeder Änderung), geschwärzte Werte erfragen,
    // Plan bauen, Prozess starten und als "starting …" zeigen. true = der Prozess wurde gestartet.
    public async Task<bool> StartSpecAsync(LaunchSpec spec)
    {
        // Simulator: nie ein echtes Programm starten, sondern einen simulierten Server mit denselben Werten hinzufügen
        if (_engine.Simulation is { } sim)
        {
            sim.StartFromLaunch(spec, TimeSpan.FromSeconds(2));
            return true;
        }
        // 1. Prüfen (nicht auf dem UI-Thread: Dateien, GGUF-Kopf, Listener-Tabelle). Fehlende Geheimnisse sind hier noch kein Fehler.
        var running = _engine.Servers.Select(s => s.Info).ToList();
        var issues = (await ServerLauncher.CheckAsync(spec, _engine.Platform, _engine.Gpu, running)).Where(i => i.Code != "secret").ToList();
        var errors = issues.Where(i => i.Severity == LaunchSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            await _prompt.InformAsync(Strings.StartTitle, Strings.StartErrors(spec.Name, string.Join("\n", errors.Select(e => "• " + e.Message))), warning: true);
            return false;
        }
        var warnings = issues.Where(i => i.Severity == LaunchSeverity.Warning).ToList();
        if (warnings.Count > 0 && !await _prompt.ConfirmAsync(Strings.StartTitle, Strings.StartWarnings(spec.Name, string.Join("\n", warnings.Select(w => "• " + w.Message))), warning: true))
            return false;

        // 2. Kommandozeile bestätigen lassen: beim ersten Start eines Profils bzw. Verlaufseintrags und nach jeder Änderung
        var lib = _engine.Library;
        string fingerprint = lib.Fingerprint(spec), key = Library.MakeKey(spec.Program, spec.Args);
        if (!lib.IsConfirmed(spec.ProfileId, key, fingerprint))
        {
            if (!await _prompt.ConfirmAsync(Strings.ConfirmCommandTitle, Strings.ConfirmCommandText(spec.Name, ServerLauncher.DescribeCommand(spec)), warning: false))
                return false;
            lib.Confirm(spec.ProfileId, key, fingerprint);
            lib.Save();
        }

        // 3. Geheimnisse: werden nie gespeichert, hier nur für diesen Start abgefragt
        var slots = ServerLauncher.FindSecretSlots(spec.Args, spec.Env);
        if (slots.Count > 0)
        {
            var byIndex = new Dictionary<int, string>();
            var byName = new Dictionary<string, string>();
            foreach (var slot in slots)
            {
                var v = await _prompt.AskSecretAsync(Strings.SecretPromptTitle(slot.Option), Strings.SecretPromptText(slot.Option));
                if (string.IsNullOrEmpty(v)) return false;
                if (slot.EnvName != null) byName[slot.EnvName] = v; else byIndex[slot.Index] = v;
            }
            spec = spec with { Args = ServerLauncher.ApplySecrets(spec.Args, byIndex), Env = ServerLauncher.ApplyEnvSecrets(spec.Env, byName) };
        }

        // 4. Plan bauen (legt --log-file an, räumt alte Logs auf) und starten
        var plan = await Task.Run(() => ServerLauncher.BuildPlan(spec, _engine.Paths.LogsDir, _engine.Settings.Limits));
        if (_engine.Registry.Launches.Any(l => l.Port == plan.Port && l.State == LaunchState.Starting))
        {
            await _prompt.InformAsync(Strings.StartTitle, Strings.AlreadyStarting);
            return false;
        }
        try
        {
            _engine.Registry.Launch(plan);
            if (spec.ProfileId is Guid id) lib.MarkStarted(id, DateTime.Now);
            lib.Save();
            return true;
        }
        catch (Exception ex)
        {
            await _prompt.InformAsync(Strings.StartTitle, Strings.StartFailed(ex.Message), warning: true);
            return false;
        }
    }

    // Profil starten (mit allen Prüfungen) und warten, bis der Server antwortet; null bei Abbruch, Fehler oder Zeitüberschreitung
    public async Task<ServerWatcher?> StartProfileAndWaitAsync(Profile p, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (RunningFor(p) is { Online: true } already) return already;
        if (!await StartProfileAsync(p)) return null;
        var end = DateTime.Now + (timeout ?? TimeSpan.FromMinutes(6));
        while (DateTime.Now < end && !ct.IsCancellationRequested)
        {
            if (RunningFor(p) is { Online: true } w) return w;
            var l = _engine.Registry.Launches.FirstOrDefault(x => x.ProfileId == p.Id);
            if (l is { State: LaunchState.Failed }) return null;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }
        return null;
    }

    // ── Stoppen ──

    // Nur nach Bestätigung mit Name und PID; PID und Startzeit stammen aus der Erkennung, der Prozess wird nie über den Namen geraten
    public async Task StopServerAsync(ServerWatcher s)
    {
        var info = s.Info;
        if (info.Manual) { await _prompt.InformAsync(Strings.StopTitle, Strings.StopManual); return; }
        if (info.Pid is not int pid) { await _prompt.InformAsync(Strings.StopTitle, Strings.StopNoProcess); return; }
        string procName = _engine.Platform.ProcessName(pid) ?? "?";
        var clients = s.Clients.Length > 0 ? Strings.StopClients(string.Join(", ", s.Clients)) : "";
        if (!await _prompt.ConfirmAsync(Strings.StopTitle, Strings.StopConfirm(s.Name, procName, pid, info.Url, clients), warning: true)) return;
        _engine.Registry.ExpectStop(s.Key);   // kein Absturz-Hinweis für einen gewollten Stopp
        await StopProcessAsync(pid, info.StartTicks);
    }

    // „Free VRAM“: alle lokalen llama.cpp-Server stoppen und alle geladenen Ollama-Modelle entladen – eine Bestätigung
    // mit der vollständigen Liste vorher. LM Studio entlädt der Monitor nicht (keine Schnittstelle dafür); es wird nur genannt.
    public async Task FreeVramAsync()
    {
        var servers = _engine.Servers;
        var stops = servers.Where(s => s.Kind == BackendKind.LlamaCpp && !s.Info.Manual && s.Info.Pid is int).ToList();
        var unloads = servers.Where(s => s.Kind == BackendKind.Ollama && s.Online)
                             .SelectMany(s => s.Models.Select(m => (Server: s, Model: m.Name))).ToList();
        var lmStudio = servers.Where(s => s.Kind == BackendKind.LmStudio && s.Online && s.Models.Count > 0).ToList();
        if (stops.Count == 0 && unloads.Count == 0)
        {
            await _prompt.InformAsync(Strings.FreeVramTitle, lmStudio.Count > 0 ? Strings.FreeVramOnlyLmStudio : Strings.FreeVramNothing);
            return;
        }
        var lines = stops.Select(s => Strings.FreeVramStopLine(s.Name, s.Info.Url, s.VramGb))
            .Concat(unloads.Select(u => Strings.FreeVramUnloadLine(u.Model, u.Server.Name)))
            .ToList();
        if (lmStudio.Count > 0) lines.Add(Strings.FreeVramLmStudioNote);
        if (!await _prompt.ConfirmAsync(Strings.FreeVramTitle, Strings.FreeVramConfirm(string.Join("\n", lines)), warning: true)) return;

        int failed = 0;
        foreach (var s in stops)
        {
            if (s.Info.Pid is not int pid) continue;
            _engine.Registry.ExpectStop(s.Key);
            if (!await StopProcessAsync(pid, s.Info.StartTicks)) failed++;
        }
        foreach (var (server, model) in unloads)
            if (!await server.UnloadAsync(model)) failed++;
        if (failed > 0) await _prompt.InformAsync(Strings.FreeVramTitle, Strings.FreeVramFailed(failed));
    }

    // Ein selbst gestarteter Server, der nach der Frist nicht lauscht, aber noch läuft
    public async Task StopLaunchedAsync(LaunchedServer l)
    {
        if (l.Pid == 0) return;
        string procName = _engine.Platform.ProcessName(l.Pid) ?? "?";
        if (!await _prompt.ConfirmAsync(Strings.StopTitle, Strings.StopConfirm(l.Name, procName, l.Pid, l.Url, ""), warning: true)) return;
        if (await StopProcessAsync(l.Pid, l.StartTicks)) _engine.Registry.DismissLaunch(l.Id);
    }

    private async Task<bool> StopProcessAsync(int pid, long startTicks)
    {
        // Die Plattform öffnet den Prozess einmal, prüft die Startzeit und beendet über dasselbe Handle
        var (outcome, error) = await Task.Run(() => { var o = ServerLauncher.Stop(_engine.Platform, pid, startTicks, out var e); return (o, e); });
        if (outcome == StopOutcome.NeedsConfirmation)
        {
            // Startzeit nicht lesbar: nur mit zweiter, ausdrücklicher Bestätigung
            if (!await _prompt.ConfirmAsync(Strings.StopTitle, Strings.StopUnverifiedText, warning: true)) return false;
            (outcome, error) = await Task.Run(() => { var o = ServerLauncher.Stop(_engine.Platform, pid, startTicks, out var e, allowUnverified: true); return (o, e); });
        }
        switch (outcome)
        {
            case StopOutcome.ProcessChanged:
            case StopOutcome.NotFound:
                await _prompt.InformAsync(Strings.StopTitle, Strings.StopChanged);
                return false;
            case StopOutcome.Failed:
                await _prompt.InformAsync(Strings.StopTitle, Strings.StopFailed(error ?? "?"), warning: true);
                return false;
            default:
                return true;
        }
    }

    // Ollama: Modell entladen (keep_alive = 0), nur nach Bestätigung
    public async Task UnloadModelAsync(ServerWatcher s, string model)
    {
        if (!await _prompt.ConfirmAsync(Strings.UnloadTitle, Strings.UnloadConfirm(model), warning: true)) return;
        if (!await s.UnloadAsync(model)) await _prompt.InformAsync(Strings.UnloadTitle, Strings.UnloadFailed(model));
    }

    // Leerlauf-Entladen: **ohne** Rückfrage, denn das Profil hat es so eingestellt. llama.cpp wird
    // gestoppt, bei Ollama/LM Studio das geladene Modell entladen (bleibt der Server selbst laufen). Gibt true zurück,
    // wenn etwas passiert ist.
    public async Task<bool> UnloadIfIdleAsync(ServerWatcher s)
    {
        if (s.Info.Manual) return false;   // von Hand eingetragen und nie gestartet: gehören dem Nutzer
        try
        {
            if (s.Kind == BackendKind.Ollama || s.Kind == BackendKind.LmStudio)
            {
                var model = s.Models.FirstOrDefault(m => m.Name.Length > 0)?.Name;
                if (model == null) return false;
                return await s.UnloadAsync(model);
            }
            await StopServerAsync(s);
            return true;
        }
        catch { return false; }
    }
}
