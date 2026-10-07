namespace StykkerLlm.Core;

// Der Server lebt nur, solange ihn jemand braucht: ein offenes Fenster (StykkerUI), eine TUI, eine offene Webseite
// (Blazor-Verbindung). Fenster und TUI melden sich regelmäßig
// (POST /api/hold, Mietdauer Lease); Webseiten zählen über ihre Verbindung.
// Ist niemand mehr da und läuft keine Arbeit (Modelltest, Benchmark), beendet sich der Server nach Grace –
// nach einem ausdrücklichen Abmelden des Letzten schon nach QuickGrace. Ein frisch gestarteter Server wartet
// StartGrace lang auf den ersten Halter (das Fenster startet ihn und meldet sich danach erst an).
public sealed class ServerHolds
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan QuickGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _leases = new(StringComparer.Ordinal);
    private readonly DateTime _started;
    private int _circuits;
    private DateTime? _emptySince;
    private bool _released;    // der letzte Halter hat sich ausdrücklich abgemeldet

    public ServerHolds(DateTime started) => _started = started;

    // Fenster/TUI: "ui:<id>", "tui:<id>"
    public void Touch(string id, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate) { _leases[id] = now + Lease; _released = false; }
    }

    public void Release(string id)
    {
        lock (_gate) { if (_leases.Remove(id)) _released = true; }
    }

    public void CircuitUp() { lock (_gate) { _circuits++; _released = false; } }
    public void CircuitDown() { lock (_gate) _circuits = Math.Max(0, _circuits - 1); }

    public int Count(DateTime now)
    {
        lock (_gate)
        {
            foreach (var k in _leases.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _leases.Remove(k);
            return _leases.Count + _circuits;
        }
    }

    // Wer gerade hält (für Status und Log): "ui", "tui", "web" mit Anzahl
    public string Describe(DateTime now)
    {
        lock (_gate)
        {
            var parts = _leases.Where(p => p.Value > now).GroupBy(p => p.Key.Split(':')[0]).Select(g => $"{g.Key} {g.Count()}").ToList();
            if (_circuits > 0) parts.Add($"web {_circuits}");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
    }

    // busy: ein Modelltest oder Benchmark läuft – dann nie beenden
    public bool ShouldStop(DateTime now, bool busy)
    {
        bool empty = Count(now) == 0 && !busy;
        lock (_gate)
        {
            if (!empty) { _emptySince = null; return false; }
            if (now - _started < StartGrace && !_released) return false;
            _emptySince ??= now;
            return now - _emptySince.Value >= (_released ? QuickGrace : Grace);
        }
    }
}
