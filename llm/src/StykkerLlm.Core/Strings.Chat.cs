namespace StykkerLlm.Core;

// Seite /prompt: Markdown-Antworten, Kopieren, Fehlergründe in der Sprache der Oberfläche (PromptHarness.Reason) – English
public static partial class Strings
{
    public const string NetRefused = "connection refused – is the server running on this port?",
        NetNoHost = "host name not found",
        NetTimedOut = "no answer from the host (timeout)",
        NetReset = "the server closed the connection",
        NetUnreachable = "the network or host cannot be reached",
        NetTls = "the https connection failed (certificate or protocol)";

    public static string PromptTimedOut(TimeSpan limit) =>
        $"No complete answer within {(limit.TotalMinutes >= 1 ? limit.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " min" : limit.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s")}. Stop and send again, or ask for a shorter answer.";

    public const string ChatCopy = "Copy", ChatCopied = "Copied", ChatCopyAnswer = "Copy this answer as Markdown",
        ChatCopyCode = "Copy this code", ChatCopyAll = "Copy chat", ChatCopyAllTip = "Copy the whole conversation as Markdown",
        ChatRestored = "Nothing came back. Your message is back in the box – send it again when the server is ready.",
        ChatEmpty = "Ask the model something. Answers show as Markdown with code blocks you can copy; the figures under each answer are time to first token, tokens, speed and why it stopped.",
        ChatCodeOpen = "still writing …";

    // Zwei Modelle vergleichen (PromptCompare)
    public const string CmpToggle = "Compare with a second model",
        CmpSecond = "Second server or model",
        CmpHint = "Each message goes to both at the same time; the answers stand side by side. Tools are off while comparing.",
        CmpNoAnswerYet = "The second model answers here.";

    public static string CmpStart(string who, double fastMs, double slowMs) =>
        $"faster start: {who} ({fastMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} vs {slowMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} ms)";
    public static string CmpStartTie(double aMs, double bMs) =>
        $"start about equal ({aMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} / {bMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} ms)";
    public static string CmpOutput(string who, double fast, double slow) =>
        $"faster output: {who} ({fast.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} vs {slow.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} t/s)";
    public static string CmpOutputTie(double a, double b) =>
        $"output about equal ({a.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} / {b.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} t/s)";
}
