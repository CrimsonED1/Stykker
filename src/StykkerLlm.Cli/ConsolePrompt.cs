using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// Rückfragen des Core (Start bestätigen, Stopp bestätigen, Geheimnis eingeben) im Terminal.
// Standardantwort ist immer "Nein". Ohne Terminal (Pipe, Skript) wird nie still zugestimmt: dann nur mit --yes.
// Geheimnisse gibt es nur interaktiv, verdeckt und ohne Speichern; --yes beantwortet sie nicht.
public interface ICliPrompt : IUserPrompt
{
    bool Declined { get; }
    bool Failed { get; }
    // vor jedem Befehl: Declined/Failed zurücksetzen (die interaktive Oberfläche führt viele Befehle in einer Sitzung aus)
    void Reset();
}

public sealed class ConsolePrompt : ICliPrompt
{
    private readonly bool _yes;
    public bool Interactive { get; }
    // Der Nutzer (oder fehlendes --yes ohne Terminal) hat abgelehnt → Exit-Code 4
    public bool Declined { get; private set; }
    // Es gab einen Fehler- oder Warnhinweis (Start fehlgeschlagen, Stopp fehlgeschlagen …) → Exit-Code 1
    public bool Failed { get; private set; }

    public void Reset() { Declined = false; Failed = false; }

    public ConsolePrompt(bool yes, bool? interactive = null)
    {
        _yes = yes;
        Interactive = interactive ?? (!Console.IsInputRedirected && !Console.IsErrorRedirected);
    }

    public Task<bool> ConfirmAsync(string title, string text, bool warning = false)
    {
        var err = Console.Error;
        err.WriteLine();
        err.WriteLine(warning ? Out.Yellow(title) : Out.Bold(title));
        err.WriteLine(text);
        if (_yes) { err.WriteLine(Out.Dim("→ yes (--yes)")); return Task.FromResult(true); }
        if (!Interactive)
        {
            err.WriteLine(Out.Dim("→ no (not a terminal; pass --yes to confirm)"));
            Declined = true;
            return Task.FromResult(false);
        }
        err.Write("Continue? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        bool ok = answer is "y" or "yes";
        if (!ok) Declined = true;
        return Task.FromResult(ok);
    }

    public Task<string?> AskSecretAsync(string title, string text)
    {
        var err = Console.Error;
        err.WriteLine();
        err.WriteLine(Out.Bold(title));
        err.WriteLine(text);
        if (!Interactive)
        {
            err.WriteLine(Out.Dim("→ cancelled (a secret can only be typed in a terminal)"));
            Declined = true;
            return Task.FromResult<string?>(null);
        }
        err.Write("Value (hidden, empty = cancel): ");
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Escape) { sb.Clear(); break; }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        err.WriteLine();
        if (sb.Length == 0) { Declined = true; return Task.FromResult<string?>(null); }
        return Task.FromResult<string?>(sb.ToString());
    }

    public Task InformAsync(string title, string text, bool warning = false)
    {
        if (warning) Failed = true;
        var err = Console.Error;
        err.WriteLine();
        err.WriteLine(warning ? Out.Yellow(title) : Out.Bold(title));
        err.WriteLine(text);
        return Task.CompletedTask;
    }
}
