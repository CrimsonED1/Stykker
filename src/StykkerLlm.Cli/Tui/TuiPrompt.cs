namespace StykkerLlm.Cli.Tui;

// Rückfragen des Core (Start/Stopp bestätigen, Geheimnis) in der Eingabezeile der Oberfläche statt mit ReadLine.
// Der Befehl wartet auf die Antwort, die Oberfläche läuft währenddessen weiter. Standardantwort Nein; Esc = Nein/Abbruch.
public sealed class TuiPrompt : ICliPrompt
{
    public sealed class Question
    {
        public required string Label { get; init; }
        public bool Secret { get; init; }
        public bool Warning { get; init; }
        internal TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private volatile Question? _pending;
    public Question? Pending => _pending;
    public bool Declined { get; private set; }
    public bool Failed { get; private set; }
    public void Reset() { Declined = false; Failed = false; }

    // Antwort aus der Eingabezeile (null = Esc)
    public void Answer(string? text)
    {
        var q = _pending;
        if (q == null) return;
        _pending = null;
        q.Answer.TrySetResult(text);
    }

    private async Task<string?> Ask(Question q)
    {
        _pending = q;
        return await q.Answer.Task.ConfigureAwait(false);
    }

    public async Task<bool> ConfirmAsync(string title, string text, bool warning = false)
    {
        Console.Out.WriteLine(warning ? Out.Yellow(title) : Out.Bold(title));
        Console.Out.WriteLine(text);
        var a = (await Ask(new Question { Label = "Continue? [y/N]", Warning = warning }))?.Trim().ToLowerInvariant();
        bool ok = a is "y" or "yes";
        Console.Out.WriteLine(Out.Dim(ok ? "→ yes" : "→ no"));
        if (!ok) Declined = true;
        return ok;
    }

    public async Task<string?> AskSecretAsync(string title, string text)
    {
        Console.Out.WriteLine(Out.Bold(title));
        Console.Out.WriteLine(text);
        var v = await Ask(new Question { Label = "Value (hidden, empty = cancel)", Secret = true });
        if (string.IsNullOrEmpty(v)) { Declined = true; Console.Out.WriteLine(Out.Dim("→ cancelled")); return null; }
        return v;
    }

    public Task InformAsync(string title, string text, bool warning = false)
    {
        if (warning) Failed = true;
        Console.Out.WriteLine(warning ? Out.Yellow(title) : Out.Bold(title));
        Console.Out.WriteLine(text);
        return Task.CompletedTask;
    }
}
