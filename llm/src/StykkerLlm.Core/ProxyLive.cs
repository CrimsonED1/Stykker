namespace StykkerLlm.Core;

// Was eine laufende Anfrage gerade tut: wartet auf das erste Token, denkt, antwortet oder ruft ein Werkzeug auf
public enum ProxyLivePhase { Waiting, Thinking, Answering, Tool }

// Momentaufnahme einer laufenden Anfrage für die Anzeige. ServerKey wie ServerWatcher.Key (null = noch nicht geroutet),
// Client wie in der Anfragenliste (erster Teil des User-Agent, sonst leer). PhaseSince = Beginn der aktuellen Phase.
public sealed record ProxyLiveRequest(string? ServerKey, string Client, ProxyLivePhase Phase, string? Tool, DateTime Started, DateTime PhaseSince,
    int ReasoningDeltas, int ContentDeltas, int ToolCalls);

// Phase samt Beginn und Werkzeugname; unveränderlich, damit ein Leser nie halb geschriebene Werte sieht
public sealed record ProxyLiveState(ProxyLivePhase Phase, DateTime Since, string? Tool)
{
    public static ProxyLiveState Initial(DateTime now) => new(ProxyLivePhase.Waiting, now, null);
}

public static class ProxyLive
{
    // Neuer Zustand nach einem Delta der Art „kind“: die Phase ist die Art des letzten Deltas. Der Beginn ändert sich nur
    // bei einem Phasenwechsel; ein Werkzeugname bleibt stehen, bis ein neuer kommt (Argument-Deltas tragen keinen Namen).
    public static ProxyLiveState Next(ProxyLiveState s, ProxyLivePhase kind, DateTime now, string? tool = null)
    {
        if (kind == ProxyLivePhase.Waiting) return s;
        if (kind != ProxyLivePhase.Tool) return s.Phase == kind ? s : new ProxyLiveState(kind, now, null);
        string? name = tool is { Length: > 0 } ? tool : s.Phase == ProxyLivePhase.Tool ? s.Tool : null;
        if (s.Phase == ProxyLivePhase.Tool) return name == s.Tool ? s : s with { Tool = name };
        return new ProxyLiveState(ProxyLivePhase.Tool, now, name);
    }
}
