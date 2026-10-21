namespace StykkerStarter.Core.Crash;

// Was nach einer Beobachtung zu tun ist
public enum CrashAction
{
    None,           // nichts zu tun
    Crashed,        // abgestürzt, ohne Neustart (die Einstellung ist aus)
    RestartPending, // abgestürzt, Neustart folgt nach Wait
    Restart,        // jetzt neu starten
    GaveUp,         // zu viele Neustarts in kurzer Zeit: nicht mehr neu starten
}

public sealed record CrashDecision(CrashAction Action, TimeSpan Wait);

// Entscheidet, ob ein Programm abgestürzt ist. Absturz = das Programm lief (vom Starter gestartet, oder beim Start schon
// laufend und mit dem Starter startend), und beim Beobachten ist kein Prozess mehr da, ohne dass der Starter oder der
// Benutzer es gestoppt hat. Die Zeit kommt von außen (now). Die Umsetzung (Prozessliste, Starten) macht der Aufrufer.
public sealed class CrashWatch
{
    public const int MaxRestarts = 5;                        // mehr als fünf Neustarte in zehn Minuten: aufgeben
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan[] Pauses = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60) };

    // Status für die Seite: null, "crashed", "restarting" oder "gaveUp"
    private sealed class Entry
    {
        public bool Expected;                  // das Programm soll laufen (der Starter hat es gestartet, oder es lief)
        public DateTime? RestartDue;           // wann der nächste Neustart fällig ist
        public string? State;
        public readonly List<DateTime> Restarts = new();   // Zeitpunkte der Neustarte (auch erfolglose)
    }

    private readonly Dictionary<string, Entry> _entries = new();

    private Entry Get(string id)
    {
        if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry();
        return e;
    }

    public string? StateOf(string id) => _entries.TryGetValue(id, out var e) ? e.State : null;

    // Wie viele Neustarte in den letzten zehn Minuten (für die Anzeige)
    public int RestartsIn(string id, DateTime now)
    {
        if (!_entries.TryGetValue(id, out var e)) return 0;
        Prune(e, now);
        return e.Restarts.Count;
    }

    // Gewollt gestoppt (Stop, Stop all, Update, Beenden): kein Absturz, und kein Neustart ist vorgemerkt
    public void Intend(string id)
    {
        var e = Get(id);
        e.Expected = false;
        e.RestartDue = null;
        e.State = null;
    }

    // Der Starter oder der Benutzer hat das Programm gestartet. manual: von Hand gestartet, das setzt die Zähler zurück.
    public void Started(string id, bool manual)
    {
        var e = Get(id);
        e.Expected = true;
        e.RestartDue = null;
        e.State = null;
        if (manual) e.Restarts.Clear();
    }

    // Eine Beobachtung. running: ein Prozess aus dem Ordner läuft. startWithStarter: das Programm startet mit dem
    // Starter (dann gilt es als erwartet, wenn es läuft). restartOnCrash: nach einem Absturz neu starten.
    public CrashDecision Observe(string id, bool running, bool startWithStarter, bool restartOnCrash, DateTime now)
    {
        var e = Get(id);
        if (running)
        {
            if (startWithStarter) e.Expected = true;
            e.RestartDue = null;
            e.State = null;
            return new CrashDecision(CrashAction.None, TimeSpan.Zero);
        }

        if (e.RestartDue is { } due)
        {
            if (now < due) return new CrashDecision(CrashAction.None, due - now);
            // fällig: der Neustart zählt ab jetzt, auch wenn er nicht gelingt (dann kommt die nächste Beobachtung)
            e.RestartDue = null;
            e.Restarts.Add(now);
            e.Expected = true;
            e.State = "restarting";
            return new CrashDecision(CrashAction.Restart, TimeSpan.Zero);
        }

        if (!e.Expected) return new CrashDecision(CrashAction.None, TimeSpan.Zero);

        // Ein Ende, das niemand veranlasst hat: abgestürzt
        e.Expected = false;
        if (!restartOnCrash)
        {
            e.State = "crashed";
            return new CrashDecision(CrashAction.Crashed, TimeSpan.Zero);
        }

        Prune(e, now);
        if (e.Restarts.Count >= MaxRestarts)
        {
            e.State = "gaveUp";
            return new CrashDecision(CrashAction.GaveUp, TimeSpan.Zero);
        }

        var wait = Pauses[Math.Min(e.Restarts.Count, Pauses.Length - 1)];
        e.RestartDue = now + wait;
        e.State = "restarting";
        return new CrashDecision(CrashAction.RestartPending, wait);
    }

    private static void Prune(Entry e, DateTime now) => e.Restarts.RemoveAll(t => now - t > Window);
}
