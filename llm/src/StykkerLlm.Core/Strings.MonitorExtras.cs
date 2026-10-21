namespace StykkerLlm.Core;

// Texte der Monitor-Erweiterungen: „Connect a client“ (ClientSnippets), die aufklappbaren Anfragen mit Modellfilter und der
// Kontext-Hinweis je Profil (ContextFit). Die Seite zeigt nur; was hier steht, ist in Web und TUI gleich.
public static partial class Strings
{
    // Proxy-Karte: Knopf und Ausklappfläche mit den Client-Blöcken
    public const string MxConnectButton = "Connect a client", MxConnectHide = "Hide client setup", MxShellLabel = "Shell",
        MxShellPowerShell = "PowerShell", MxShellBash = "bash",
        MxClaudeCode = "Claude Code",
        MxClaudeCodeHint = "Set these before starting Claude Code in a console. The model name is the one the Stykker-Proxy routes to its current target.",
        MxOpenAi = "Qwen Code / OpenAI-compatible",
        MxOpenAiHint = "For Qwen Code or any OpenAI-compatible client. The model name is stykker.",
        MxCurl = "curl (chat completions)", MxCurlHint = "One request to /v1/chat/completions. Run it in bash or Git Bash.",
        MxCopyBlock = "Copy";

    // Letzte Anfragen: Filter und aufklappbare Einzelheiten
    public const string MxModelFilter = "Model", MxAllModels = "All", MxDetails = "Details",
        MxRoute = "Route", MxClientLabel = "Client", MxUserAgent = "User agent", MxPromptTokens = "Prompt tokens",
        MxOutputTokens = "Output tokens", MxCacheLabel = "Cache", MxToolsLabel = "Tools", MxThinkLabel = "Thinking tokens",
        MxStatusLabel = "Status", MxHttpStatus = "HTTP status", MxStarted = "Started", MxEnded = "Ended", MxDuration = "Duration",
        MxNoValue = "–", MxNoRoute = "not through the Stykker-Proxy", MxRouteDefault = "Default target",
        MxRouteNamed = "Model asked for by name", MxNoUserAgent = "not sent";

    // Profile: Kontext, der in den freien VRAM passt
    public const string MxNoContext = "no context fits in free VRAM",
        MxCtxFitTip = "Estimated from the GGUF header: KV cache per token (layers, KV heads, head size, cache type) against free VRAM " +
                      "minus the model on the GPU. Rounded down to 1k. Display only: starting is not blocked.";

    public static string MxCtxFit(int ctx) => $"fits up to ctx ~{ctx / ContextFit.Step} k";

    // Lesbarer Weg einer Anfrage in der Detailzeile: Sonderwege mit Namen, Standard und ausdrücklicher Name mit eigenem Wort
    public static string MxRouteText(string? route) => route switch
    {
        null => MxNoRoute,
        ProxyRoutes.Default => MxRouteDefault,
        ProxyRoutes.Named => MxRouteNamed,
        ProxyRoutes.Classifier => ReqRouteAuto,
        ProxyRoutes.Vision => ReqRouteImage,
        _ => route,
    };
}
