using StykkerLlm.Core;

namespace StykkerLlm.Cli.Tui;

// /prompt in der TUI: Mini-Harness wie der Knopf „Prompt“ in Fenster und Web. Das Gespräch bleibt für diese
// TUI-Sitzung stehen, bis /prompt clear kommt oder ein anderer Server gewählt wird.
//   /prompt <server> <text>   an diesen Server (Name, Key oder Port)
//   /prompt <text>            an den zuletzt gewählten
//   /prompt clear | system <text> | temp <0..2> | max <n>
//   /prompt tools <ordner> | tools off   Werkzeuge (read, edit, cmd/PowerShell) im Arbeitsordner
//   /prompt auto on|off                  Schreiben, Ändern, Befehle ohne Rückfrage
internal static class PromptCommand
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly PromptSession Session = new();
    private static string _serverKey = "";

    public static async Task<int> RunAsync(Session s, CliArgs a, CancellationToken ct)
    {
        var o = Console.Out;
        var words = a.Words.ToList();
        if (words.Count == 0) { Out.Error(Strings.PromptUsage); return Commands.Usage; }
        switch (words[0].ToLowerInvariant())
        {
            case "clear":
                Session.Clear();
                o.WriteLine(Out.Dim(Strings.BtnClear));
                return Commands.Ok;
            case "system":
                Session.Options.System = string.Join(' ', words.Skip(1));
                return Commands.Ok;
            case "temp" when words.Count > 1 && double.TryParse(words[1], System.Globalization.NumberStyles.Float, Strings.Inv, out var t):
                Session.Options.Temperature = Math.Clamp(t, 0, 2);
                return Commands.Ok;
            case "max" when words.Count > 1 && int.TryParse(words[1], out var m):
                Session.Options.MaxTokens = Math.Clamp(m, 1, 32768);
                return Commands.Ok;
            case "tools":
            {
                var arg = string.Join(' ', words.Skip(1)).Trim().Trim('"');
                if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)) { Session.Options.Tools = null; o.WriteLine(Out.Dim("tools off")); return Commands.Ok; }
                if (!Directory.Exists(arg)) { Out.Error(Strings.PromptNoWorkdir); return Commands.Usage; }
                Session.Options.Tools = new PromptTools(arg);
                o.WriteLine(Out.Green(Strings.PromptToolsOn) + Out.Dim("  " + Session.Options.Tools.Root));
                o.WriteLine(Out.Dim(Strings.PromptToolsHint));
                return Commands.Ok;
            }
            case "auto" when words.Count > 1:
                Session.Options.AutoApprove = words[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                o.WriteLine(Out.Dim(Strings.PromptAutoApprove + ": " + (Session.Options.AutoApprove ? Strings.On : "off")));
                return Commands.Ok;
        }

        var servers = s.Server?.Servers.Where(x => x.Online).ToList() ?? new List<RemoteServer>();
        var named = Find(servers, words[0]);
        if (named != null)
        {
            words.RemoveAt(0);
            if (named.Key != _serverKey) { Session.Clear(); _serverKey = named.Key; Session.Options.Model = PromptHarness.ModelFor(named); }
        }
        var server = servers.FirstOrDefault(x => x.Key == _serverKey) ?? (servers.Count == 1 ? servers[0] : null);
        if (server == null) { Out.Error(servers.Count == 0 ? Strings.PromptNoServer : Strings.PromptUsage); return Commands.Usage; }
        if (server.Key != _serverKey) { _serverKey = server.Key; Session.Options.Model = PromptHarness.ModelFor(server); }
        var text = string.Join(' ', words);
        if (text.Length == 0) { Out.Error(Strings.PromptUsage); return Commands.Usage; }

        o.WriteLine(Out.Cyan(Strings.PromptYou + ": ") + text);
        int shownReasoning = 0, shownText = 0, shownTools = 0;
        bool thinkingHeader = false, answerHeader = false;
        var r = await Session.SendAsync(Http, server.Url, text, () =>
        {
            // Werkzeugaufrufe und ihre Ergebnisse, sobald sie da sind
            while (shownTools < Session.ToolLog.Count)
            {
                var t = Session.ToolLog[shownTools++];
                if (shownText > 0 || shownReasoning > 0) o.WriteLine();
                o.WriteLine(Out.Yellow("⚙ " + t.Call.Summary) + (t.Denied ? Out.Red("  " + Strings.ToolDenied) : ""));
                if (!t.Denied) o.WriteLine(Out.Dim(t.Result.Length > 600 ? t.Result[..600] + " …" : t.Result));
                shownText = shownReasoning = 0;
                thinkingHeader = answerHeader = false;
            }
            // Nur das Neue ausgeben; Denken grau vor der Antwort
            var rs = Session.LiveReasoning;
            if (rs.Length > shownReasoning)
            {
                if (!thinkingHeader) { o.Write(Out.Dim(Strings.PromptThinking + ": ")); thinkingHeader = true; }
                o.Write(Out.Dim(rs[shownReasoning..]));
                shownReasoning = rs.Length;
            }
            var tx = Session.LiveText;
            if (tx.Length > shownText)
            {
                if (!answerHeader) { if (thinkingHeader) o.WriteLine(); o.Write(Out.Bold(server.Name + ": ")); answerHeader = true; }
                o.Write(tx[shownText..]);
                shownText = tx.Length;
            }
        }, ct, async call =>
        {
            // Freigabe in der TUI: Rückfrage mit dem ganzen Aufruf
            var args = call.Arguments.Length > 1500 ? call.Arguments[..1500] + " …" : call.Arguments;
            return await s.Prompt.ConfirmAsync(Strings.ToolApproveTitle, call.Summary + "\n" + args, warning: true).ConfigureAwait(false);
        }).ConfigureAwait(false);
        o.WriteLine();
        o.WriteLine(r.Ok ? Out.Dim(PromptHarness.Stats(r)) : Out.Red(r.Error ?? ""));
        return r.Ok ? Commands.Ok : Commands.Error;
    }

    private static RemoteServer? Find(List<RemoteServer> servers, string word) =>
        servers.FirstOrDefault(x => x.Name.Equals(word, StringComparison.OrdinalIgnoreCase) || x.Key.Equals(word, StringComparison.OrdinalIgnoreCase))
        ?? (int.TryParse(word, out var port) ? servers.FirstOrDefault(x => x.Port == port) : null);
}
