namespace StykkerLlm.Core;

// Ein Benchmark als Dienst, damit Fenster, Web und die Steuer-API denselben Ablauf haben (S6/S3):
// Ziel starten falls nötig, während des Laufs aufzeichnen, messen, speichern, Rückgang melden.
public sealed class BenchmarkService
{
    private CancellationTokenSource? _cts;
    public bool Running { get; private set; }
    public BenchResult? Current { get; private set; }
    public string Status { get; private set; } = "";
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
        Status = "";
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = _cts.Token;
        RecordingSession? session = null;
        BenchResult? result = null;
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
                // jetzt sind Kontext und Slots des laufenden Servers bekannt
                options.ContextPerSlot = Math.Max(1, (server.Props?.NCtx ?? options.ContextPerSlot * options.Slots) / Math.Max(1, options.Slots));
                options.Slots = Math.Max(1, server.Props?.TotalSlots ?? options.Slots);
            }
            options.ApiKey = server.Info.ApiKey;
            if (!engine.Recorder.IsRecording(server.Key))
                session = engine.Recorder.Start(server.Key, engine.Servers, engine.Gpu?.Name ?? "", false);
            result = BenchmarkRunner.Describe(server, engine.Library, engine.Gpu?.Name ?? "", engine.Platform.GpuDriver);
            Current = result;
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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
            Running = false;
            if (result != null) Finished?.Invoke(result, regression);
            // Scheitert der Lauf vor dem ersten Ergebnis, bleibt der Grund in Status stehen (sonst meldet die
            // Steuer-API nur "error:" ohne Text); der Lauf beginnt ohnehin mit leerem Status.
        }
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
