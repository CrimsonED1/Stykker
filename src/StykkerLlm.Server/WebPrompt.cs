using StykkerLlm.Core;

namespace StykkerLlm.Server;

// Rückfragen des Core (Start/Stop bestätigen, Geheimnis eingeben, Hinweise) im Browser: je Sitzung (Blazor-Circuit) ein Dialog.
// Der Core wartet auf die Antwort; der Dialog (PromptDialog im Layout) zeigt die offene Frage und beantwortet sie.
public sealed class WebPrompt : IUserPrompt
{
    public sealed record Request(string Kind, string Title, string Text, bool Warning, TaskCompletionSource<string?> Answer);
    public Request? Pending { get; private set; }
    public event Action? Changed;

    private async Task<string?> Ask(string kind, string title, string text, bool warning)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending = new Request(kind, title, text, warning, tcs);
        Changed?.Invoke();
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { Pending = null; Changed?.Invoke(); }
    }

    public void Answer(string? value) => Pending?.Answer.TrySetResult(value);

    public async Task<bool> ConfirmAsync(string title, string text, bool warning = false) => await Ask("confirm", title, text, warning) == "yes";
    public Task<string?> AskSecretAsync(string title, string text) => Ask("secret", title, text, false);
    public Task InformAsync(string title, string text, bool warning = false) => Ask("info", title, text, warning);
}
