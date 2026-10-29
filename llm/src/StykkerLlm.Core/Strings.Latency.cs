namespace StykkerLlm.Core;

// Prompt-Verarbeitung und Zeit bis zum ersten Token (ServerLatency): Serverkarte und „Recent requests“ – English
public static partial class Strings
{
    public const string CardPromptTps = "Prompt processing: median tokens per second while reading the prompt (last requests)",
        LblPromptTps = "prompt t/s", LblTtft = "first token",
        ReqTtft = "ttft",
        ReqTtftTip = "Time to first token: from the start of the request to the first thinking or answer token, as the Stykker proxy saw it (includes waiting in the queue and reading the prompt).",
        ReqPromptTip = "Prompt processing in tokens per second (from the server's log)",
        MxTtft = "Time to first token", MxPromptSpeed = "Prompt processing";

    public static string CardTtft(int samples) =>
        $"Time to first token: median of the last {samples} requests through the Stykker proxy (includes queue and prompt reading)";
    public static string CardTtftEstimated(int samples) =>
        $"Time to first token, estimated: median prompt reading time of the last {samples} requests from the server's log. Requests through the Stykker proxy give the measured value with the queue.";
}
