using StykkerFirewall.Core;

namespace StykkerFirewall.Service;

// Startet das Backend, gleicht die Regeln ab und reicht blockierte Versuche an den Koordinator weiter.
// Einmal pro Minute ein erneuter Abgleich: räumt abgelaufene Regeln ab und holt Regeln nach, die jemand in der
// Windows-Firewall von Hand gelöscht hat.
public sealed class FirewallWorker(FirewallCoordinator coordinator, ServiceOptions options, ILogger<FirewallWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Backend {Backend}, data in {DataDir}", coordinator.Backend.DisplayName, options.ResolveDataDir());
        try
        {
            await coordinator.StartAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ohne Rechte oder ohne Firewall läuft der Dienst weiter, damit die Oberfläche den Grund zeigen kann.
            log.LogError(ex, "Backend start failed");
        }

        var watch = WatchAsync(stoppingToken);
        // Alle 5 s: unbestätigtes Ask zurückstellen (Schutz gegen Aussperren). Jede Minute: Regeln abgleichen.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        int tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (await coordinator.CheckModeRevertAsync(stoppingToken))
                        log.LogWarning("Ask mode was not confirmed in time; switched back to Monitor");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Switching back to Monitor failed"); }

                if (++tick % 12 != 0) continue;
                try { await coordinator.ReconcileAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Reconcile failed"); }
            }
        }
        catch (OperationCanceledException) { }
        await watch;
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await coordinator.RunAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "Watching blocked connections failed; retrying in 10 s");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
