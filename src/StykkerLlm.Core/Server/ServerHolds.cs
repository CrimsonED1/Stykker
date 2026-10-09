using System.Diagnostics;

namespace StykkerLlm.Core;

// Der Server lebt nur, solange ihn jemand braucht: ein offenes Fenster (StykkerUI), eine TUI, eine offene Webseite
// (Blazor-Verbindung). Fenster und TUI melden sich regelmäßig
// (POST /api/hold, Mietdauer Lease); Webseiten zählen über ihre Verbindung.
// Ist niemand mehr da und läuft keine Arbeit (Modelltest, Benchmark), beendet sich der Server nach Grace –
// nach einem ausdrücklichen Abmelden des Letzten schon nach QuickGrace. Ein frisch gestarteter Server wartet
// StartGrace lang auf den ersten Halter (das Fenster startet ihn und meldet sich danach erst an).
// Ein Fenster oder eine TUI, deren Prozess ohne Abmelden endet (Absturz, geschlossene Konsole), hält nichts mehr:
// das zählt wie ein ausdrückliches Abmelden (QuickGrace).
public sealed class ServerHolds
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan QuickGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _leases = new(StringComparer.Ordinal);
    private readonly DateTime _started;
    private readonly Func<int, bool>? _processRunning;
    private int _circuits;
    private DateTime? _emptySince;
    private bool _released;    // der letzte Halter hat sich ausdrücklich abgemeldet

    // processRunning prüft, ob der Prozess hinter einer Miete ("ui:1234") noch läuft; ohne Prüfung gilt nur die Miete
    public ServerHolds(DateTime started, Func<int, bool>? processRunning = null)
    {
        _started = started;
        _processRunning = processRunning;
    }

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
            // Ein Fenster oder eine TUI ohne laufenden Prozess meldet sich nicht mehr ab: das zählt wie ein Abmelden
            if (_processRunning != null)
                foreach (var k in _leases.Keys.ToList())
                    if (ProcessIdOf(k) is { } pid && !_processRunning(pid)) { _leases.Remove(k); _released = true; }
            return _leases.Count + _circuits;
        }
    }

    // "ui:1234" -> 1234; eine Miete ohne Prozessnummer gilt nur bis zu ihrem Ablauf
    private static int? ProcessIdOf(string id) => int.TryParse(id.Split(':')[^1], out var pid) ? (int?)pid : null;

    // Läuft der Prozess noch? Unklar (keine Rechte, anderer Fehler) gilt er als laufend: lieber zu lange halten als zu früh beenden
    public static bool ProcessRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception) { return true; }
    }

    // Was passiert, wenn das Fenster `leaving` jetzt geht. ownPages: die Webseiten, die es selbst offen hält (1, solange
    // seine Seite steht). Gezählt werden nur die anderen, lebenden Mieten und Webseiten; verändert wird nichts.
    public ShutdownOutlook Outlook(DateTime now, string leaving, int ownPages, bool keepRunning, bool busy, int proxyActive)
    {
        lock (_gate)
        {
            int windows = 0, terminals = 0;
            foreach (var (id, until) in _leases)
            {
                if (id == leaving || until <= now || !Alive(id)) continue;
                if (id.StartsWith("ui:", StringComparison.Ordinal)) windows++;
                else terminals++;                        // tui und unbekannte Mieten zählen wie in Count
            }
            return new ShutdownOutlook(windows, terminals, Math.Max(0, _circuits - ownPages), keepRunning, busy, proxyActive);
        }
    }

    // Läuft der Prozess hinter einer Miete noch? Ohne Prozessnummer oder ohne Prüfung gilt die Miete
    private bool Alive(string id)
    {
        if (_processRunning == null) return true;
        var pid = ProcessIdOf(id);
        return pid is not int n || _processRunning(n);
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
