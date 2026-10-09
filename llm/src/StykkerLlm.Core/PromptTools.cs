using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Werkzeuge für den Mini-Harness: das Modell darf in einem Arbeitsordner lesen, auflisten, schreiben, ändern und
// Befehle ausführen (cmd oder PowerShell) – die Grundausstattung eines Coding-Agenten, um lokale Modelle darauf zu
// prüfen. Schutz: Pfade bleiben im Arbeitsordner; Schreiben, Ändern und Befehle brauchen eine Freigabe je Aufruf
// (außer „ohne Rückfrage“). Ein Befehl läuft im Arbeitsordner, kann aber grundsätzlich überall wirken – deshalb
// ist die Freigabe der eigentliche Schutz, nicht der Ordner.

public sealed record ToolCall(string Id, string Name, string Arguments)
{
    public bool NeedsApproval => Name is "write_file" or "edit_file" or "run_command";

    // Für die Anzeige und die Rückfrage: "run_command (powershell): Get-ChildItem"
    public string Summary
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(Arguments.Length == 0 ? "{}" : Arguments);
                var a = doc.RootElement;
                string S(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                return Name switch
                {
                    "run_command" => $"{Name} ({(S("shell").Length > 0 ? S("shell") : "cmd")}): {S("command")}",
                    "edit_file" => $"{Name}: {S("path")}  − {Cut(S("old_text"), 60)}  + {Cut(S("new_text"), 60)}",
                    "write_file" => $"{Name}: {S("path")} ({S("content").Length} chars)",
                    _ => $"{Name}: {S("path")}",
                };
            }
            catch (JsonException) { return $"{Name}: {Cut(Arguments, 120)}"; }
        }
    }

    private static string Cut(string s, int n) => s.Length <= n ? s.ReplaceLineEndings("⏎") : s[..n].ReplaceLineEndings("⏎") + "…";
}

public sealed record ToolOutcome(ToolCall Call, string Result, bool Ok, bool Denied);

public sealed class PromptTools
{
    public const int MaxOutput = 12_000;
    public const int MaxReadBytes = 200_000;
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    public string Root { get; }

    public PromptTools(string root) => Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    // Die Beschreibung für das Modell (OpenAI "tools")
    public static JsonArray Definitions()
    {
        static JsonObject Fn(string name, string desc, JsonObject props, params string[] required) => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name, ["description"] = desc,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) },
            },
        };
        static JsonObject P(string type, string desc) => new() { ["type"] = type, ["description"] = desc };
        return new JsonArray
        {
            Fn("list_dir", "List files and folders. Paths are relative to the working folder.", new JsonObject { ["path"] = P("string", "folder, '.' for the working folder") }, "path"),
            Fn("read_file", "Read a text file (optionally a range of lines).", new JsonObject
            {
                ["path"] = P("string", "file path"), ["offset"] = P("integer", "first line, 1-based (optional)"), ["limit"] = P("integer", "number of lines (optional)"),
            }, "path"),
            Fn("write_file", "Create or overwrite a text file with the given content.", new JsonObject { ["path"] = P("string", "file path"), ["content"] = P("string", "full file content") }, "path", "content"),
            Fn("edit_file", "Replace one exact occurrence of old_text with new_text in a file.", new JsonObject
            {
                ["path"] = P("string", "file path"), ["old_text"] = P("string", "text to find (must occur exactly once)"), ["new_text"] = P("string", "replacement"),
            }, "path", "old_text", "new_text"),
            Fn("run_command", "Run a shell command in the working folder and return its output (60 s limit).", new JsonObject
            {
                ["command"] = P("string", "the command line"), ["shell"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("cmd", "powershell"), ["description"] = "cmd (default) or powershell" },
            }, "command"),
        };
    }

    // Pfad im Arbeitsordner auflösen; null = außerhalb (auch über .., Laufwerksbuchstaben oder Links im Namen)
    public string? Resolve(string? path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0 || p == ".") return Root.TrimEnd(Path.DirectorySeparatorChar);
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(Root, p)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var withSep = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return withSep.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct = default)
    {
        JsonElement a;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments);
            a = doc.RootElement;
        }
        catch (JsonException) { return Done(Strings.ToolBadArguments, false); }
        using var _ = doc;
        string S(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int I(string n) => a.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : 0;
        try
        {
            switch (call.Name)
            {
                case "list_dir":
                {
                    var dir = Resolve(S("path"));
                    if (dir == null) return Done(Strings.ToolOutside, false);
                    if (!Directory.Exists(dir)) return Done(Strings.ToolNotFound(S("path")), false);
                    var sb = new StringBuilder();
                    foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x)) sb.Append(Path.GetFileName(d)).Append("/\n");
                    foreach (var f in Directory.GetFiles(dir).OrderBy(x => x)) sb.Append(Path.GetFileName(f)).Append("  (").Append(new FileInfo(f).Length).Append(" bytes)\n");
                    return Done(sb.Length == 0 ? "(empty)" : sb.ToString(), true);
                }
                case "read_file":
                {
                    var file = Resolve(S("path"));
                    if (file == null) return Done(Strings.ToolOutside, false);
                    if (!File.Exists(file)) return Done(Strings.ToolNotFound(S("path")), false);
                    if (new FileInfo(file).Length > MaxReadBytes && I("limit") == 0) return Done(Strings.ToolTooBig(MaxReadBytes), false);
                    var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
                    int from = Math.Max(1, I("offset")), count = I("limit") > 0 ? I("limit") : lines.Length;
                    var sel = lines.Skip(from - 1).Take(count).Select((l, i) => $"{from + i,5}  {l}");
                    return Done(string.Join('\n', sel), true);
                }
                case "write_file":
                {
                    var file = Resolve(S("path"));
                    if (file == null) return Done(Strings.ToolOutside, false);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, S("content"), ct).ConfigureAwait(false);
                    return Done(Strings.ToolWritten(S("path"), S("content").Length), true);
                }
                case "edit_file":
                {
                    var file = Resolve(S("path"));
                    if (file == null) return Done(Strings.ToolOutside, false);
                    if (!File.Exists(file)) return Done(Strings.ToolNotFound(S("path")), false);
                    var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var old = S("old_text");
                    int count = old.Length == 0 ? 0 : CountOf(text, old);
                    if (count != 1) return Done(Strings.ToolEditCount(count), false);
                    await File.WriteAllTextAsync(file, text.Replace(old, S("new_text")), ct).ConfigureAwait(false);
                    return Done(Strings.ToolEdited(S("path")), true);
                }
                case "run_command":
                    return await CommandAsync(S("command"), S("shell"), ct).ConfigureAwait(false) with { Call = call };
                default:
                    return Done(Strings.ToolUnknown(call.Name), false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Done(ex.Message, false);
        }

        ToolOutcome Done(string result, bool ok) => new(call, Trim(result), ok, false);
    }

    private static int CountOf(string text, string part)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
        return n;
    }

    private async Task<ToolOutcome> CommandAsync(string command, string shell, CancellationToken ct)
    {
        var call = new ToolCall("", "run_command", "");
        if (command.Trim().Length == 0) return new(call, Strings.ToolBadArguments, false, false);
        bool ps = shell.Equals("powershell", StringComparison.OrdinalIgnoreCase) || shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
        var psi = ps
            ? new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command } }
            : OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.WorkingDirectory = Root;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;            // keine Rückfrage kann hängen bleiben: Eingabe ist sofort zu
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        void Collect(string? line) { if (line == null) return; lock (output) if (output.Length < MaxOutput * 2) output.Append(line).Append('\n'); }
        p.OutputDataReceived += (_, e) => Collect(e.Data);
        p.ErrorDataReceived += (_, e) => Collect(e.Data);
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { return new(call, ex.Message, false, false); }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CommandTimeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(call, Trim(output + "\n" + Strings.ToolTimeout), false, false);
        }
        p.WaitForExit();   // Rest der Ausgabe abholen
        string text;
        lock (output) text = output.ToString();
        return new(call, Trim($"exit code {p.ExitCode}\n{text}"), p.ExitCode == 0, false);
    }

    private static string Trim(string s) => s.Length <= MaxOutput ? s : s[..MaxOutput] + "\n" + Strings.ToolTruncated;
}
