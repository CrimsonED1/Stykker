namespace StykkerLlm.Server;

// Der offene Client-Dialog. Die Kopfleiste (Chips) und die Proxy-Karte im Monitor öffnen ihn; er selbst steht einmal
// in der Layout-Seite (wie der Befehlsdialog). Ein Dienst je Verbindung, so wie WebPrompt.
public sealed class ClientDialogs
{
    // Kennung des Clients, dessen Dialog offen ist; null = zu
    public string? OpenId { get; private set; }

    public event Action? Changed;

    public void Open(string id)
    {
        OpenId = id;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (OpenId == null) return;
        OpenId = null;
        Changed?.Invoke();
    }
}
