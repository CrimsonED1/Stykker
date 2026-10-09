namespace StykkerLlm.Core;

// Automatischer Neustart nach einem Absturz, **opt-in je Profil**: `Profile.RestartOnCrash` ist
// standardmäßig aus, weil ein Neustart im Kreis VRAM und Strom verbrauchen kann, ohne dass es jemand bemerkt.
//
// Die Regel steht hier im Core und ist rein: sie entscheidet anhand von Profil und Zählerstand, ob ein Neustart
// erlaubt ist, damit sie ohne Fenster, Server oder Simulator prüfbar bleibt. Wer startet, ist Sache des Aufrufers
// (der Server hat Engine und Startkoordinator zusammen).
public sealed class CrashRestartPolicy
{
    /// <summary>Wie viele Neustarts eines Profils im Zeitfenster erlaubt sind (1–10, Standard 3).</summary>
    public int MaxRestarts { get; set; } = 3;
    /// <summary>Zurückgezählt wird nach dieser Zeit (Standard 10 min).</summary>
    public int WindowMinutes { get; set; } = 10;
    /// <summary>Pause vor dem Neustart, damit der Port erst wieder frei wird (Standard 3 s).</summary>
    public int DelaySeconds { get; set; } = 3;

    private readonly Dictionary<string, List<DateTimeOffset>> _tries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int MaxRestartsClamped => Math.Clamp(MaxRestarts, 0, 10);
    public int WindowMinutesClamped => Math.Clamp(WindowMinutes, 1, 120);
    public int DelaySecondsClamped => Math.Clamp(DelaySeconds, 0, 120);

    /// <summary>Darf dieses Profil jetzt neu gestartet werden? Zähler im Fenster werden dabei neu gezählt.</summary>
    public bool Allows(string key, Profile? profile, DateTimeOffset now)
    {
        if (profile == null || !profile.RestartOnCrash || key.Length == 0) return false;
        if (profile.MaxRestarts is int m && m > 0) MaxRestarts = m;   // am Profil gewünschte Grenze
        int max = MaxRestartsClamped;
        if (max == 0) return false;

        lock (_lock)
        {
            var window = TimeSpan.FromMinutes(WindowMinutesClamped);
            if (!_tries.TryGetValue(key, out var times)) _tries[key] = times = new List<DateTimeOffset>();
            times.RemoveAll(t => now - t > window);
            if (times.Count >= max) return false;
            times.Add(now);
            return true;
        }
    }

    /// <summary>Zählerstand für die Anzeige (Neustarts im Fenster).</summary>
    public (int Used, int Max) Count(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_tries.TryGetValue(key, out var times)) return (0, MaxRestartsClamped);
            var window = TimeSpan.FromMinutes(WindowMinutesClamped);
            return (times.Count(t => now - t <= window), MaxRestartsClamped);
        }
    }

    /// <summary>Nach einem erfolgreichen Lauf (oder wenn der Nutzer selbst gestartet hat) zählt neu.</summary>
    public void Reset(string key)
    {
        lock (_lock) _tries.Remove(key);
    }
}