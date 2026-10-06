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
}