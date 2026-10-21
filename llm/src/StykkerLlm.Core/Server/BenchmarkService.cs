namespace StykkerLlm.Core;

// Ein Benchmark als Dienst, damit Fenster, Web und die Steuer-API denselben Ablauf haben (S6/S3):
// Ziel starten falls nötig, während des Laufs aufzeichnen, messen, speichern, Rückgang melden.
public sealed class BenchmarkService
{
    private CancellationTokenSource? _cts;
    public bool Running { get; private set; }
    public BenchResult? Current { get; private set; }
    public BenchOptions? CurrentOptions { get; private set; }
    public string Status { get; private set; } = "";
    // Restzeit des laufenden Ergebnisses in ganzen Minuten (null: noch keine Messung)
    public int? MinutesLeft => Running && Current != null && CurrentOptions != null ? BenchmarkRunner.MinutesLeft(CurrentOptions, Current) : null;
    // (Meldung, abgeschlossener Schritt) – die Oberfläche malt beides
    public event Action<string, BenchStep?>? Progress;
    public event Action<BenchResult, BenchRegression?>? Finished;
    // Abbruch aus der Oberfläche (der Aufrufer kann auch sein eigenes Token mitgeben)
    public void Cancel()
    {
        _cts?.Cancel();
        StateJson.Notice(Strings.BenchCancelling);
    }

    // Ziel: vorhandener Server (Key) oder gespeichertes Profil (Id), das dafür gestartet wird
    public async Task<BenchResult?> RunAsync(MonitorEngine engine, LaunchCoordinator launcher, string targetKey, BenchOptions options,
        CancellationToken outer = default)
    {
        if (Running) return null;
        if (!options.Chat && options.ContextSizes.Length == 0 && !options.Tool && !options.Parallel) return null;
        Running = true;
        CurrentOptions = options;
        Status = "";
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = _cts.Token;
        RecordingSession? session = null;
        BenchResult? result = null;
        ServerWatcher? startedHere = null;
        BenchRegression? regression = null;
        try
        {
            var server = engine.Servers.FirstOrDefault(s => s.Key == targetKey);
            if (server == null)
            {
                var profile = FindProfile(engine.Library, targetKey);
                if (profile == null) { Say(Strings.BenchStartFailed); return null; }
                Say(Strings.BenchStarting(profile.Name));
                server = await launcher.StartProfileAndWaitAsync(profile, null, ct);
                if (server == null) { Say(Strings.BenchStartFailed); return null; }
                startedHere = server;
                // jetzt sind Kontext und Slots des laufenden Servers bekannt
                options.ContextPerSlot = Math.Max(1, (server.Props?.NCtx ?? options.ContextPerSlot * options.Slots) / Math.Max(1, options.Slots));
                options.Slots = Math.Max(1, server.Props?.TotalSlots ?? options.Slots);
            }
            // Ein laufender Server meldet seine Slots in /props: die parallelen Anfragen richten sich danach (wie bei einem gestarteten Profil)
            if (server.Props is { TotalSlots: > 0 } running) options.Slots = running.TotalSlots;
            // Kontext je Slot unbekannt (Seite ohne /props-Angabe): aus den Slots des Servers, sonst entfiele die Kontext-Leiter
            if (options.ContextPerSlot <= 0)
            {
                int perSlot = server.Slots.Count > 0 ? server.Slots.Max(v => v.CtxMax) : (server.Props?.NCtx ?? 0) / Math.Max(1, server.Props?.TotalSlots ?? 1);
                if (perSlot > 0) { options.ContextPerSlot = perSlot; options.Slots = Math.Max(options.Slots, server.Slots.Count); }
            }
            options.ApiKey = server.Info.ApiKey;
            if (!engine.Recorder.IsRecording(server.Key))
                session = engine.Recorder.Start(server.Key, engine.Servers, engine.Gpu?.Name ?? "", false);
            result = BenchmarkRunner.Describe(server, engine.Library, engine.Gpu?.Name ?? "", engine.Platform.GpuDriver);
            Current = result;
            using var http = engine.BenchClient?.Invoke() ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            await BenchmarkRunner.RunAsync(http, server.Url, options, result,
                msg => Say(msg),
                step => Progress?.Invoke("", step),
                ct);
            if (session != null)
            {
                result.RecordingFile = session.File;
                engine.Recorder.Stop(session);
                session = null;
            }
            // Rückgang vor dem Speichern festhalten: er gehört zum Ergebnis und steht damit in allen Oberflächen
            if (!result.Cancelled)
            {
                try
                {
                    var previous = BenchmarkRunner.PreviousSimilar(engine.Library, result);
                    if (previous != null && BenchmarkRunner.Regression(previous, result, engine.Settings) is { } reg)
                    {
                        regression = reg;
                        result.Regression = reg;
                    }
                }
                catch { }
            }
            engine.Library.AddBenchmark(result);
            engine.Library.Save();
            if (!result.Cancelled)
            {
                Say(Strings.BenchDone(Fmt.Dur(result.DurationSec)));
                if (regression is { } r) Say(Strings.BenchRegressionAlert(r.DropPct, r.Previous, r.Current));
            }
            else Say(Strings.BenchCancelled);
            return result;
        }
        catch (OperationCanceledException) { Say(Strings.BenchCancelled); return result; }
        catch (Exception ex) { Say(Strings.BenchFailed(ex.Message)); return result; }
        finally
        {
            if (session != null) engine.Recorder.Stop(session);
            if (startedHere != null && options.StopAfter && await launcher.StopStartedAsync(startedHere)) Say(Strings.BenchStoppedAfter(startedHere.Name));
            Running = false;
            if (result != null) Finished?.Invoke(result, regression);
            // Scheitert der Lauf vor dem ersten Ergebnis, bleibt der Grund in Status stehen (sonst meldet die
            // Steuer-API nur "error:" ohne Text); der Lauf beginnt ohnehin mit leerem Status.
        }
    }

    // Kontext je Slot des Ziels, soweit bekannt: laufender Server aus den Slots bzw. /props, Profil aus -c und -np
    public static int? KnownContextPerSlot(MonitorEngine engine, string targetKey)
    {
        var server = engine.Servers.FirstOrDefault(s => s.Key == targetKey);
        if (server != null)
        {
            if (server.Slots.Count > 0 && server.Slots.Max(v => v.CtxMax) is > 0 and var m) return m;
            if (server.Props is { NCtx: > 0 } p) return p.NCtx / Math.Max(1, p.TotalSlots);
            return PerSlot(server.Info.Args);
        }
        var profile = FindProfile(engine.Library, targetKey);
        return profile == null ? null : PerSlot(profile.Args);
    }

    // llama-server teilt -c auf die Slots auf (-np); ohne -c ist der Kontext unbekannt
    public static int? PerSlot(IReadOnlyList<string> args)
    {
        var a = LlamaServerArgs.Parse(args);
        return a.Ctx is > 0 and var c ? c / Math.Max(1, a.Np ?? 1) : null;
    }

    // Ist das Ziel ein gespeichertes Profil, das (noch) nicht läuft? Dann wird es für den Lauf gestartet.
    public static bool WillStart(MonitorEngine engine, string targetKey) =>
        !engine.Servers.Any(s => s.Key == targetKey) && FindProfile(engine.Library, targetKey) != null;

    // Slots des gewählten Ziels, soweit bekannt: laufender Server aus /props (sonst -np), gespeichertes Profil aus den Startargumenten
    public static int? KnownSlots(MonitorEngine engine, string targetKey)
    {
        var server = engine.Servers.FirstOrDefault(s => s.Key == targetKey);
        if (server != null)
            return server.Props is { TotalSlots: > 0 } p ? p.TotalSlots : BenchmarkRunner.SlotsFromArgs(server.Info.Args);
        var profile = FindProfile(engine.Library, targetKey);
        return profile == null ? null : BenchmarkRunner.SlotsFromArgs(profile.Args);
    }

    private static Profile? FindProfile(Library lib, string idOrName)
    {
        if (Guid.TryParse(idOrName, out var id)) return lib.Profiles.FirstOrDefault(p => p.Id == id);
        return lib.Profiles.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            ?? lib.Profiles.FirstOrDefault(p => p.Key == idOrName);
    }

    private void Say(string text)
    {
        Status = text;
        Progress?.Invoke(text, null);
    }
}
