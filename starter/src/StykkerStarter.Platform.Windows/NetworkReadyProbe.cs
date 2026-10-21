using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using StykkerStarter.Core.Ready;

namespace StykkerStarter.Platform.Windows;

// „Läuft heißt bereit“ auf dieser Maschine: eine TCP-Verbindung zu 127.0.0.1:<Port>, oder eine GET-Abfrage der Adresse,
// bereit bei einer Antwort mit Status unter 500. Ohne Proxy (es geht nur um localhost). Jeder Versuch hat die Zeit timeout.
public sealed class NetworkReadyProbe : IReadyProbe
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false });

    public bool IsReady(ReadyTarget target, TimeSpan timeout) =>
        target.IsUrl ? HttpOk(target.Url!, timeout) : TcpOpen(target.Port, timeout);

    private static bool TcpOpen(int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            return connect.Wait(timeout) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool HttpOk(Uri url, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            using var response = Http.GetAsync(url, cancel.Token).GetAwaiter().GetResult();
            return (int)response.StatusCode < 500;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}
