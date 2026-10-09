using StykkerLlm.Core;

namespace StykkerLlm.Server;

// Kurze Meldungen unten rechts (docs/plan-ui-redesign.md, U8/U10), je Browser-Verbindung. Eine Meldung kann „Rückgängig“
// anbieten: dann läuft die eigentliche Aktion erst, wenn die Meldung ohne Klick abläuft (Commit), sonst nie.
public sealed class Toasts
{
    public sealed class Toast
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string Text { get; init; }
        public bool Warn { get; init; }
        public Func<Task>? Commit { get; init; }     // mit Rückgängig: was nach Ablauf passiert
        public Action? Undo { get; init; }
        public DateTime Until { get; init; }
    }

    private readonly List<Toast> _items = new();
    public event Action? Changed;

    public IReadOnlyList<Toast> Items { get { lock (_items) return _items.ToList(); } }

    public void Show(string text, bool warn = false, int seconds = 4) => Add(new Toast { Text = text, Warn = warn, Until = DateTime.Now.AddSeconds(seconds) });

    // Aktion mit Rückgängig: commit läuft nach Ablauf (Vorgabe 6 s), undo sofort beim Klick
    public void ShowUndo(string text, Func<Task> commit, Action? undo = null, int seconds = 6) =>
        Add(new Toast { Text = text, Commit = commit, Undo = undo, Until = DateTime.Now.AddSeconds(seconds) });

    private void Add(Toast t)
    {
        lock (_items) _items.Add(t);
        Changed?.Invoke();
        _ = Task.Delay(t.Until - DateTime.Now).ContinueWith(async _ => await ExpireAsync(t.Id));
    }

    public void UndoNow(Guid id)
    {
        Toast? t;
        lock (_items) { t = _items.FirstOrDefault(x => x.Id == id); if (t != null) _items.Remove(t); }
        t?.Undo?.Invoke();
        Changed?.Invoke();
    }

    public void Dismiss(Guid id) => _ = ExpireAsync(id);

    private async Task ExpireAsync(Guid id)
    {
        Toast? t;
        lock (_items) { t = _items.FirstOrDefault(x => x.Id == id); if (t != null) _items.Remove(t); }
        if (t == null) return;
        Changed?.Invoke();
        if (t.Commit != null)
        {
            try { await t.Commit(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Error("toast commit", ex); }
        }
    }
}
