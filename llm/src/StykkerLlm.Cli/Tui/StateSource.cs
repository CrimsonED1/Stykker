using StykkerLlm.Core;

namespace StykkerLlm.Cli.Tui;

// Woher die Anzeige ihren Stand bekommt. Im Betrieb vom Server (er misst); im Debug-Build und in den Tests aus dem
// Simulator, ohne Server und ohne Netz.
public interface IStateSource : IDisposable
{
    // null = noch kein Stand (Server startet, Verbindung weg); Status sagt dann, was los ist
    Task<StateSnapshot?> PollAsync(CancellationToken ct);
    string Status { get; }
    int Port { get; }
}

// Der Server: verbinden, sonst starten; solange die TUI offen ist, hält sie ihn (ServerHolds) – ohne sie endet er
public sealed class ServerSource : IStateSource
{
    private readonly CliArgs _args;
    private readonly string _holdId = "tui:" + Environment.ProcessId;
    private ServerClient? _client;
    private Task<ServerClient>? _connecting;
    private Timer? _hold;

    public ServerSource(CliArgs args) => _args = args;
    public string Status { get; private set; } = Strings.ShellConnecting;
    public int Port => _args.Port;

    public async Task<StateSnapshot?> PollAsync(CancellationToken ct)
    {
        if (_client == null)
        {
            _connecting ??= ServerLink.EnsureAsync(_args, s => Status = s, ct);
            if (!_connecting.IsCompleted) return null;
            try
            {
                _client = await _connecting.ConfigureAwait(false);
                var c = _client;
                _hold = new Timer(_ => { _ = c.HoldAsync(_holdId); }, null, 0, 5000);
                Status = "";
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            {
                Status = ex.Message;
                AppLog.Write("tui: " + ex.Message);
                return null;
            }
            finally { _connecting = null; }
        }
        var state = await _client.GetStateAsync(ct).ConfigureAwait(false);
        if (state == null)
        {
            // Server weg (beendet im Web, abgestürzt): beim nächsten Takt neu verbinden bzw. starten
            Status = Strings.ServerGone;
            _hold?.Dispose(); _hold = null;
            _client.Dispose(); _client = null;
        }
        return state;
    }

    public void Dispose()
    {
        _hold?.Dispose();
        if (_client != null)
        {
            try { _client.ReleaseAsync(_holdId).Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _client.Dispose();
        }
    }
}

// Der Simulator als Quelle (Debug-Schalter --sim, Tests): eigene Welt und Engine, der Stand wie vom Server
public sealed class SimSource : IStateSource
{
    private readonly SimHost _sim = new(SimServerSpec.Defaults());

    public SimSource() => _sim.World.Start();
    public string Status => "";
    public int Port => 17400;
    public MonitorEngine Engine => _sim.Engine;

    public async Task<StateSnapshot?> PollAsync(CancellationToken ct)
    {
        await _sim.Engine.TickAsync().ConfigureAwait(false);
        return StateSnapshot.Parse(StateJson.WriteText(_sim.Engine, null, null, Port, DateTimeOffset.Now, withHistory: true));
    }

    public void Dispose() => _sim.Dispose();
}
