using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Ipc;

namespace StykkerFirewall.Service;

// Named Pipe für die Oberfläche. Zugriff: SYSTEM und Administratoren voll, angemeldete (interaktive) Nutzer lesen
// und schreiben. Jeder verbundene Client bekommt neue Nachfragen, neue Aktivität und nach jeder Änderung einen
// frischen Snapshot.
public sealed class PipeServer(FirewallCoordinator coordinator, ServiceOptions options, ILogger<PipeServer> log) : BackgroundService
{
    // Ohne Puffer (0) wartet jedes Schreiben, bis der Client liest.
    private const int BufferSize = 64 * 1024;

    private readonly ConcurrentDictionary<Guid, IpcChannel> _clients = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        coordinator.PromptRaised += p => Broadcast(new PromptRaised(p));
        coordinator.ActivityAdded += a => Broadcast(new ActivityAdded(a));
        coordinator.StateChanged += () => _ = BroadcastSnapshotAsync(stoppingToken);

        log.LogInformation("Listening on pipe {Pipe}", options.Pipe);
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(options.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferSize, BufferSize, CreateSecurity());
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex)
            {
                log.LogError(ex, "Pipe could not be created");
                try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = ServeAsync(pipe, stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var channel = new IpcChannel(pipe);
        _clients[id] = channel;
        try
        {
            await channel.SendAsync(new Snapshot(await coordinator.GetSnapshotAsync(ct)), ct);
            while (!ct.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(ct);
                if (message is null) break;
                try
                {
                    await HandleAsync(channel, message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Request {Type} failed", message.GetType().Name);
                    await channel.SendAsync(new ErrorMessage(ex.Message), ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Text.Json.JsonException)
        {
            // Client weg oder kaputte Nachricht: Verbindung schließen.
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    private async Task HandleAsync(IpcChannel channel, IpcMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case GetSnapshot:
                await channel.SendAsync(new Snapshot(await coordinator.GetSnapshotAsync(ct)), ct);
                break;
            case Decide d:
                await coordinator.DecideAsync(d.PromptId, d.Decision, ct);
                break;
            case DeleteRule r:
                await coordinator.DeleteRuleAsync(r.RuleId, ct);
                break;
            case SetMode m:
                await coordinator.SetModeAsync(m.Mode, ct);
                break;
            case ConfirmMode:
                coordinator.ConfirmMode();
                break;
            default:
                await channel.SendAsync(new ErrorMessage($"Unexpected message {message.GetType().Name}"), ct);
                break;
        }
    }

    private async Task BroadcastSnapshotAsync(CancellationToken ct)
    {
        try { Broadcast(new Snapshot(await coordinator.GetSnapshotAsync(ct))); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Snapshot failed"); }
    }

    private void Broadcast(IpcMessage message)
    {
        foreach (var (id, client) in _clients)
        {
            _ = client.SendAsync(message, CancellationToken.None).ContinueWith(
                t => _clients.TryRemove(id, out _), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Als Konsole gestartet (Probelauf): der eigene Nutzer muss den Server-Endpunkt für weitere Instanzen anlegen dürfen.
        using var me = WindowsIdentity.GetCurrent();
        if (me.User is { } user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
