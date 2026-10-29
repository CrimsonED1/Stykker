namespace StykkerLlm.Core;

// Karte „Activity“ im Monitor (ActivityLog) – English
public static partial class Strings
{
    public const string ActivityTitle = "Activity",
        ActivityNone = "Nothing happened yet. Crashes, restarts, idle unloads and finished model tests or benchmarks show up here with their time.",
        ActivityTip = "What happened while you were not looking: crashes with their cause, restarts, idle unloads, finished model tests and benchmarks. Kept until the server restarts.";

    public static string ActivityUnloadDetail(int minutes) => $"no request for {minutes} min";
}
