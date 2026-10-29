namespace StykkerLlm.Core;

// Texte des Failovers im Proxy (Ersatzziele, Warten auf den Neustart): Proxy-Dialog Reiter „Routing“, Kennzeichen der Anfragen, Proxy-Karte
public static partial class Strings
{
    public const string ReqRouteFallback = "fallback", ReqRouteRetry = "retry";
    public const string ReqRouteFallbackTip = "The default model could not take this request (offline, crashed, loading or too little context); a fallback model answered.";
    public const string ReqRouteRetryTip = "The model did not answer at first (crash or restart); the proxy waited and sent the request again.";

    public const string ProxyFallbackSection = "Fallback";
    public const string ProxyFallbackHint = "When the default model is offline, crashes, is still loading or has too little context for a request, "
        + "the proxy sends the request to the first fallback that can take it. The client never sees the error.";
    public const string ProxyFallbackNone = "No fallback";
    public static string ProxyFallbackLabel(int n) => $"Fallback {n.ToString(Inv)}";
    public const string ProxyFallbackSet = "Fallback set", ProxyFallbackCleared = "Fallback removed";
    public const string ProxyRestartWaitLabel = "Wait for a crashed model to come back (up to 30 s) before answering with an error";
    public const string ProxyRestartWaitOn = "The proxy waits for a crashed model", ProxyRestartWaitOff = "The proxy answers at once when a model does not answer";

    // Proxy-Karte, Zeile „Routed“: gerettete Anfragen seit dem Start des Servers
    public static string PxRescued(int n) => $" · rescued {n.ToString(Inv)}";
    public const string PxRescuedTip = "rescued: requests that would have failed (model offline, crashed or too small) and were answered by a fallback or after a restart.";
}
