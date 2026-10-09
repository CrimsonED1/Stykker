using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// stykker bugreport <was passiert ist>  –  Zip mit Protokollen, Einstellungen und Zustand (ohne Geheimnisse) und
// die Adresse für ein neues GitHub-Issue. Läuft der Server, baut er den Bericht (dann ist sein Zustand dabei).
public static class BugReportCommand
{
    public static async Task<int> RunAsync(CliArgs a, CancellationToken ct)
    {
        var text = string.Join(' ', a.Words).Trim();
        if (text.Length == 0)
        {
            Out.Error(Strings.BugReportEmpty + "  (stykker bugreport <what happened>)");
            return Program.Usage;
        }
        string zip, url;
        using (var client = await ServerLink.ConnectAsync(a, ct).ConfigureAwait(false))
        {
            if (client?.State != null)
            {
                var r = await client.SendAsync("bugreport.create", text, ct: ct).ConfigureAwait(false);
                if (!r.Ok || r.Data == null) { Out.Error(r.Message); return Program.Error; }
                var parts = r.Data.Split('\n');
                zip = parts[0];
                url = parts.Length > 1 ? parts[1] : "";
            }
            else
            {
                var report = BugReport.Create(a.Paths.Get(), text, null, DateTime.Now);
                zip = report.ZipPath;
                url = report.IssueUrl;
            }
        }
        AppLog.Write($"bug report: {zip}");
        Console.Out.WriteLine($"{Out.Green("saved")}  {zip}");
        Console.Out.WriteLine(Strings.BugReportIssueHint);
        Console.Out.WriteLine(url);
        return Program.Ok;
    }
}
