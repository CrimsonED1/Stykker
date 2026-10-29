namespace StykkerLlm.Core;

// Leerlauf-Entladen: **opt-in je Profil** – `Profile.UnloadAfterIdleMin`, 0 = aus. Nach so vielen
// Minuten ohne Anfrage wird der Server gestoppt (llama.cpp) bzw. das Modell entladen (Ollama, LM Studio).
//
// Opt-in, weil es den Server wegnimmt, während jemand noch redet: ein Standardwert könnte einem laufenden Modell
// unter den Füßen wegziehen. Die Regel steht rein im Core und ist ohne Fenster, Server oder Simulator prüfbar;
// wer stoppt, entscheidet der Aufrufer (der Server hat den Startkoordinator).
public static class IdleUnload
{
    /// <summary>Wie viele Minuten Leerlauf das Profil verträgt (0 = aus, höchstens 12 Stunden).</summary>
    public static int MinutesOf(Profile? profile) => Math.Clamp(profile?.UnloadAfterIdleMin ?? 0, 0, 720);

    /// <summary>Soll dieser Server jetzt entladen werden?</summary>
    public static bool Due(Profile? profile, ServerWatcher w, DateTime now) =>
        Due(profile, w.Online && !w.Loading, IsBusy(w), w.LastBusyAt, w.Since, now);

    /// <summary>Die Regel selbst, ohne Server – damit sie sich ohne Simulator prüfen lässt.</summary>
    public static bool Due(Profile? profile, bool online, bool busy, DateTime? lastBusyAt, DateTime since, DateTime now)
    {
        int minutes = MinutesOf(profile);
        if (minutes <= 0 || !online || busy) return false;
        return now - (lastBusyAt ?? since) >= TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Wann wäre es fällig? Für die Anzeige im Zustand (null = nichts geplant).</summary>
    public static DateTime? DueAt(Profile? profile, ServerWatcher w) => DueAt(profile, w.LastBusyAt, w.Since);

    public static DateTime? DueAt(Profile? profile, DateTime? lastBusyAt, DateTime since)
    {
        int minutes = MinutesOf(profile);
        return minutes <= 0 ? null : (lastBusyAt ?? since).AddMinutes(minutes);
    }

    /// <summary>Läuft gerade eine Anfrage? Dann wird nie entladen – auch nicht mitten im Prompt.</summary>
    public static bool IsBusy(ServerWatcher w) =>
        w.Slots.Any(x => x.Busy) || w.Current > 0.05;

    // Ab wann die Karte „idle …“ zeigt: kurze Pausen zwischen zwei Anfragen eines Agents sind kein Leerlauf
    public static readonly TimeSpan IdleShownAfter = TimeSpan.FromMinutes(2);

    /// <summary>Leerlauf für die Karte (Idee B4): wie lange ohne Anfrage, und wann entladen wird (null = nie, Profil ohne
    /// Leerlauf-Entladen). AtLeast: der Server lief schon, als der Monitor anfing zuzusehen – die Pause ist mindestens so lang.</summary>
    public static IdleInfo? Idle(Profile? profile, bool online, bool busy, DateTime? lastBusyAt, DateTime since, DateTime now)
    {
        if (!online || busy) return null;
        var idle = now - (lastBusyAt ?? since);
        if (idle < IdleShownAfter) return null;
        TimeSpan? left = DueAt(profile, lastBusyAt, since) is DateTime due ? (due > now ? due - now : TimeSpan.Zero) : null;
        return new IdleInfo(idle, left, MinutesOf(profile), lastBusyAt == null);
    }

    public static IdleInfo? Idle(Profile? profile, ServerWatcher w, DateTime now) =>
        Idle(profile, w.Online && !w.Loading, IsBusy(w), w.LastBusyAt, w.Since, now);

    // „7 min“, „2 h 05 min“ – die Karte zeigt nur ganze Minuten
    public static string Span(TimeSpan t) =>
        t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalHours} h {t.Minutes:00} min";
}

public sealed record IdleInfo(TimeSpan Idle, TimeSpan? UnloadIn, int UnloadAfterMin, bool AtLeast)
{
    // Kurztext auf der Karte: „idle 40 min“, „idle ≥ 40 min · unload in 12 min“
    public string Text => (AtLeast ? Strings.CardIdleAtLeast(IdleUnload.Span(Idle)) : Strings.CardIdle(IdleUnload.Span(Idle)))
        + (UnloadIn is TimeSpan left ? " · " + Strings.CardUnloadIn(IdleUnload.Span(left)) : "");

    public string Tip => UnloadIn != null ? Strings.CardIdleUnloadTip(UnloadAfterMin) : Strings.CardIdleTip;

    // Gleich fällig: die Karte hebt den Hinweis hervor
    public bool Soon => UnloadIn is TimeSpan left && left <= TimeSpan.FromMinutes(2);
}