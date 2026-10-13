using System.Net.Sockets;

namespace Stykker.Shared.Config;

// Läuft ein Modul? Geprüft wird nur, ob sein Port eine Verbindung annimmt – ohne Anfrage, ohne Anmeldung. Das reicht
// für die Lampe in der Zentrale und berührt die Zugangsprüfung des Moduls nicht.
public static class HubProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(400);

    public static async Task<bool> AnswersAsync(HubModule module, string address, CancellationToken cancel = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        // Ein eigenes Modul läuft auf dieser Maschine: dort fragen, auch wenn der Aufrufer sie unter einem Netznamen sieht.
        var host = module.Port is > 0 && string.IsNullOrEmpty(module.Url) ? "127.0.0.1" : uri.Host;
        try
        {
            using var client = new TcpClient();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(Timeout);
            await client.ConnectAsync(host, uri.Port, limit.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }
}
