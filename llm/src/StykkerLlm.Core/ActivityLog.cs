namespace StykkerLlm.Core;

// Was passiert ist, während niemand hinsah (Karte „Activity“ im Monitor): Abstürze, Neustarts, Leerlauf-Entladen, fertige
// Modelltests und Benchmarks – mit Uhrzeit und Ursache. Das Banner zeigt nur den letzten verlorenen Server, die Hinweise
// der Oberflächen haben keine Zeit; hier bleiben die letzten Ereignisse stehen (nur im Speicher, ein Neustart leert sie).
public enum ActivityKind { Crash, Restart, RestartFailed, Unload, Tests, Bench }

public sealed record ActivityEvent(DateTime When, ActivityKind Kind, string Text, string? Detail = null)
{
    // Schlimm (rot): Absturz, Neustart gescheitert. Sonst Information.
    public bool Alarm => Kind is ActivityKind.Crash or ActivityKind.RestartFailed;

    // Als Meldung auf den anderen Seiten: alles außer dem Absturz, den das Banner oben auf jeder Seite schon zeigt
    public bool ShowsToast => Kind != ActivityKind.Crash;

    // Symbol aus dem Sprite (IconSprite)
    public string Icon => Kind switch
    {
        ActivityKind.Crash => "e-crash",
        ActivityKind.Restart => "restart",
        ActivityKind.RestartFailed => "r-err",
        ActivityKind.Unload => "eject",
        ActivityKind.Tests => "r-ok",
        _ => "avg",
    };
}

public sealed class ActivityLog
{
    public const int Max = 100;
    private readonly object _gate = new();
    private readonly LinkedList<ActivityEvent> _events = new();

    public event Action? Changed;

    public void Add(ActivityEvent e)
    {
        lock (_gate)
        {
            _events.AddFirst(e);
            while (_events.Count > Max) _events.RemoveLast();
        }
        Changed?.Invoke();
    }

    public void Add(ActivityKind kind, string text, string? detail = null, DateTime? when = null) =>
        Add(new ActivityEvent(when ?? DateTime.Now, kind, text, detail));

    // Die neuesten zuerst
    public IReadOnlyList<ActivityEvent> Recent(int take = 8)
    {
        lock (_gate) return _events.Take(take).ToList();
    }

    public int Count { get { lock (_gate) return _events.Count; } }

    // Was seit seen dazukam, ältestes zuerst (recent: die neuesten zuerst, wie Recent). Ist seen nicht mehr in der Liste
    // (herausgefallen) oder fehlt, gilt die ganze Liste als neu. Verglichen wird der Eintrag selbst, nicht sein Text:
    // zwei gleiche Meldungen hintereinander sind zwei Ereignisse.
    public static IReadOnlyList<ActivityEvent> Since(IReadOnlyList<ActivityEvent> recent, ActivityEvent? seen)
    {
        var fresh = new List<ActivityEvent>();
        foreach (var e in recent)
        {
            if (seen != null && ReferenceEquals(e, seen)) break;
            fresh.Add(e);
        }
        fresh.Reverse();
        return fresh;
    }

    // Ein fertiger Lauf (DoneEvents) als Eintrag
    public void Add(DoneEvent done) =>
        Add(new ActivityEvent(done.When, done.Kind == "tests" ? ActivityKind.Tests : ActivityKind.Bench, Strip(done.Title), done.Body));

    // „Stykker LLM: …“ ist für die Browser-Meldung gedacht; in der Liste steht die Marke schon darüber
    private static string Strip(string title) => title.StartsWith("Stykker LLM: ", StringComparison.Ordinal) ? title["Stykker LLM: ".Length..] : title;

    // Kurze Zeit für die Liste: heute nur die Uhrzeit, sonst mit Datum
    public static string TimeText(DateTime when, DateTime now) =>
        when.Date == now.Date ? when.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : when.ToString("dd.MM. HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
