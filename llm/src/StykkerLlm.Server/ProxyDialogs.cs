namespace StykkerLlm.Server;

// Der offene Proxy-Dialog. Der Proxy-Chip der Kopfleiste und das Zahnrad der Proxy-Karte im Monitor öffnen ihn; er selbst
// steht einmal in der Layout-Seite (wie der Client-Dialog). Ein Dienst je Verbindung, so wie ClientDialogs.
public sealed class ProxyDialogs
{
    public bool IsOpen { get; private set; }

    public event Action? Changed;

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Changed?.Invoke();
    }
}
