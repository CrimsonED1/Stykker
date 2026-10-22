namespace StykkerStarter.Core.Ready;

// Die Prüfung hinter „läuft heißt bereit“. Die Umsetzung (TCP-Verbindung, HTTP-Abfrage) steckt im Platform-Projekt;
// Tests geben Fakes.
public interface IReadyProbe
{
    bool IsReady(ReadyTarget target, TimeSpan timeout);
}

public enum ReadyState
{
    None,      // keine Prüfung (kein Eintrag, oder das Programm läuft nicht)
    Waiting,   // gestartet, noch nicht bereit (oder nach einem Fehlschlag wieder wartend)
    Ready,     // die Prüfung geht
    Timeout,   // 60 s nach dem Start (oder dem Fehlschlag) noch nicht bereit
}

// Der Zustand „bereit“ je Programm. Die Zeit kommt von außen (now), damit Tests sie vorgeben können.
// Nach dem Start wartet das Programm: bereit, sobald die Prüfung geht. Geht die Prüfung bei einem laufenden Programm
// nicht mehr, wird es wieder „wartend“; nach 60 s ohne Erfolg heißt es „Zeitüberschreitung“. Neustart gibt es deswegen
// nie.
public sealed class ReadyTracker
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, (ReadyState State, DateTime Since)> _states = new();

    // Das Programm ist gestartet: wartet, ab jetzt zählen die 60 s
    public void Started(string id, DateTime now) => _states[id] = (ReadyState.Waiting, now);

    // Das Programm läuft nicht mehr: kein Zustand
    public void Stopped(string id) => _states.Remove(id);

    public ReadyState StateOf(string id) => _states.TryGetValue(id, out var s) ? s.State : ReadyState.None;

    // Eine Prüfung ausführen und das Ergebnis eintragen. Eine Prüfung, die wirft, gilt als nicht bereit.
    public ReadyState Check(string id, ReadyTarget target, IReadyProbe probe, DateTime now)
    {
        bool ready;
        try { ready = probe.IsReady(target, ProbeTimeout); }
        catch (Exception) { ready = false; }
        return Record(id, ready, now);
    }

    // Die Zeit, die eine einzelne Prüfung höchstens dauert (je Versuch)
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    // Das Ergebnis einer Prüfung eintragen; liefert den neuen Zustand
    public ReadyState Record(string id, bool probeReady, DateTime now)
    {
        var (state, since) = _states.TryGetValue(id, out var s) ? s : (ReadyState.None, now);
        if (probeReady)
        {
            _states[id] = (ReadyState.Ready, now);
            return ReadyState.Ready;
        }

        switch (state)
        {
            case ReadyState.Ready:
                _states[id] = (ReadyState.Waiting, now);   // eine Prüfung schlägt fehl: wieder wartend, Frist neu
                break;
            case ReadyState.Waiting when now - since >= Deadline:
                _states[id] = (ReadyState.Timeout, since);
                break;
            case ReadyState.Waiting:
            case ReadyState.Timeout:
                break;
            default:
                _states[id] = (ReadyState.Waiting, now);
                break;
        }
        return _states[id].State;
    }
}
