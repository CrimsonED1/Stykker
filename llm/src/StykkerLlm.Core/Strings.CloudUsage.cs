namespace StykkerLlm.Core;

// Texte des Cloud-Verbrauchs (Proxy-Dialog, Reiter „Sources“, unter jedem Anbieter)
public static partial class Strings
{
    public static string CloudUsageLine(long todayRequests, long todayTokens, long weekRequests, long weekTokens) =>
        $"today {todayRequests} req · {CloudUsage.FormatTokens(todayTokens)} tokens · 7 days {weekRequests} req · {CloudUsage.FormatTokens(weekTokens)} tokens";
}
