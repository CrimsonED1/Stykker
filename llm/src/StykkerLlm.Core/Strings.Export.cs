namespace StykkerLlm.Core;

// Herunterladen: Profil als Startskript (ScriptExport.ForProfile) und das Anfragen-Protokoll als CSV – English
public static partial class Strings
{
    public const string PfExport = "Start script",
        PfExportTip = "Download the command line as a script to start the same server without StykkerLLM (in a repo, on another PC). API keys and tokens stay redacted as ***.",
        ReqCsvTip = "Download every request the monitor saw (requests.csv: time, server, model, client, prompt and reply tokens, speed, status). The list here shows only the last few.",
        ReqProxyCsvTip = "Download every request through the Stykker proxy (proxy.csv: requested model, target, client, HTTP status, duration, reply tokens, aborted, tool count, route).";

    // Befehlsfenster (Ctrl+K)
    public const string CmdAlerts = "Alerts in this browser: on / off";
    public static string CmdDownload(string file) => $"Download {file}";
    public static string CmdScript(string profile) => $"Start script: {profile} (PowerShell)";
}
