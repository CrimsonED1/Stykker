using System.IO;
using System.IO.Pipes;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Ipc;

namespace StykkerFirewall.App;

// Verbindung zum Dienst. Verbindet sich immer wieder neu, solange die App läuft (Dienst neu gestartet, noch nicht
// installiert …). Ereignisse kommen auf einem Hintergrund-Thread; Blazor nimmt sie mit InvokeAsync, WPF mit dem Dispatcher.
public sealed class FirewallClient : IAsyncDisposable
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private IpcChannel? _channel;
    private Task? _loop;
    private List<ActivityEntry> _recent = [];

    public FirewallClient(string pipe) => _pipe = pipe;

    public bool Connected { get; private set; }

    public string? LastError { get; private set; }

    public FirewallSnapshot? State { get; private set; }

    public IReadOnlyList<ActivityEntry> Recent { get { lock (_gate) return _recent.ToList(); } }

    public IReadOnlyList<PendingPrompt> Pending => State?.Pending ?? [];

    public event Action? Changed;

    public event Action<PendingPrompt>? PromptRaised;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public Task DecideAsync(Guid promptId, PromptDecision decision) => SendAsync(new Decide(promptId, decision));

    public Task DeleteRuleAsync(Guid ruleId) => SendAsync(new DeleteRule(ruleId));

    public Task SetModeAsync(ProtectionMode mode) => SendAsync(new SetMode(mode));

    public Task ConfirmModeAsync() => SendAsync(new ConfirmMode());

    public Task RefreshAsync()=> SendAsync(new GetSnapshot());

    private async Task SendAsync(IpcMessage message)
    {
        var channel = _channel;
        if (channel is null) { LastError = "Not connected to the StykkerFirewall service."; Changed?.Invoke(); return; }
        try { await channel.SendAsync(message, _stop.Token); }
        catch (IOException ex) { LastError = ex.Message; Changed?.Invoke(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, ct);
                await using var channel = new IpcChannel(pipe);
                _channel = channel;
                Connected = true;
                LastError = null;
                Changed?.Invoke();

                while (await channel.ReceiveAsync(ct) is { } message)
                    Handle(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                LastError = ex is TimeoutException ? "The StykkerFirewall service is not running." : ex.Message;
            }

            _channel = null;
            if (Connected) { Connected = false; }
            Changed?.Invoke();
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Handle(IpcMessage message)
    {
        switch (message)
        {
            case Snapshot s:
                lock (_gate) { State = s.State; _recent = s.State.Recent.ToList(); }
                Changed?.Invoke();
                break;
            case PromptRaised p:
                PromptRaised?.Invoke(p.Prompt);
                break;
            case ActivityAdded a:
                lock (_gate)
                {
                    _recent.Insert(0, a.Entry);
                    if (_recent.Count > 500) _recent.RemoveRange(500, _recent.Count - 500);
                }
                Changed?.Invoke();
                break;
            case ErrorMessage e:
                LastError = e.Text;
                Changed?.Invoke();
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        _stop.Dispose();
    }
}
