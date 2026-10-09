namespace StykkerLlm.Core;

// Texte des Mini-Harness (Prompt an ein geladenes Modell). Fenster, Web und TUI zeigen dieselben Sätze.
public static partial class Strings
{
    public const string BtnPrompt = "Prompt", PromptTitle = "Prompt the model";
    public const string PromptHint = "Talk to the loaded model directly (OpenAI API, streamed). Follow-up questions keep the conversation; Clear starts over.";
    public const string PromptSystem = "System prompt (optional)";
    public const string PromptTemperature = "Temperature", PromptMaxTokens = "Max tokens", PromptModel = "Model", PromptApiKey = "API key (only if the server needs one)";
    public const string PromptInput = "Your message … (Ctrl+Enter sends)";
    public const string BtnSend = "Send", BtnStopAnswer = "Stop", BtnClear = "Clear";
    public const string PromptThinking = "thinking";
    public const string PromptYou = "You", PromptModelSays = "Model";
    public const string PromptBusy = "An answer is still coming.";
    public const string PromptNeedsKey = "The server wants an API key (started with --api-key). Enter it under Advanced.";
    public const string PromptNoServer = "No server with a loaded model.";
    public const string PromptCloudNeedsProxy = "Cloud models run through the Stykker proxy. Turn the proxy on (Monitor page) to use them.";
    public static string PromptHttpError(int code, string text) => $"HTTP {code}: {text}";
    public static string PromptNotReachable(string why) => $"Server not reachable: {why}";
    // Werkzeuge
    public const string PromptToolsOn = "Tools: read, list, write, edit, cmd/PowerShell";
    public const string PromptWorkdir = "Working folder (tools stay inside)";
    public const string PromptAutoApprove = "write, edit and commands without asking";
    public const string PromptToolsHint = "Reading and listing stay in the working folder. Writing, editing and commands ask first; a command can reach beyond the folder. llama.cpp needs --jinja for tools.";
    public const string ToolApproveTitle = "The model wants to run a tool";
    public const string BtnAllow = "Allow", BtnRefuse = "Refuse";
    public const string ToolDenied = "Refused by the user.";
    public const string ToolOutside = "Path is outside the working folder.";
    public const string ToolBadArguments = "Arguments are not valid JSON or a value is missing.";
    public const string ToolTimeout = "Stopped after 60 s.";
    public const string ToolTruncated = "… (output cut)";
    public const string PromptNoWorkdir = "Pick a working folder that exists.";
    // Ordner-Wähler und erweiterte Optionen der Prompt-Seite
    public const string PromptChooseFolder = "Choose folder…", PromptUseFolder = "Use this folder", PromptFolderCancel = "Cancel";
    public const string PromptFolderUp = "Up one level", PromptFolderDrives = "Drives", PromptFolderEmpty = "No subfolders here.";
    public const string PromptFolderFailed = "This folder can't be opened.", PromptAdvanced = "Advanced";
    public static string ToolNotFound(string path) => $"Not found: {path}";
    public static string ToolTooBig(int bytes) => $"File is larger than {bytes / 1000} KB – read it with offset and limit.";
    public static string ToolWritten(string path, int chars) => $"Wrote {path} ({chars} chars).";
    public static string ToolEdited(string path) => $"Edited {path}.";
    public static string ToolEditCount(int n) => n == 0 ? "old_text was not found." : $"old_text occurs {n} times – make it unique.";
    public static string ToolUnknown(string name) => $"Unknown tool '{name}'.";
    public static string ToolTooManySteps(int n) => $"Stopped after {n} tool rounds.";

}
