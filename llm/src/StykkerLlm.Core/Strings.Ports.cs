namespace StykkerLlm.Core;

// Texte zum Port-Übergang von 8078 auf 17401 (LegacyPort)
public static partial class Strings
{
    // Antwort auf jede Anfrage auf dem alten Port, die kein Model-Host ist (410 Gone)
    public static string LegacyPortMoved(int port) => $"Stykker LLM moved to port {port}.";
    public static string LegacyPortOpen(int port) => $"The old port 8078 is open for model hosts only, until a later version removes it. Pair the hosts again with port {port}.";
    public static string LegacyPortBusy(int port) => $"Port 8078 is in use, so the old address for model hosts stays closed. Pair the hosts again with port {port}.";
    public const string LegacyHostConnected = "A model host connected over the old port 8078. Pair it again: the old port goes away in a later version.";
}
