using System.Net;
using System.Net.Sockets;
using Stykker.Pairing;

namespace StykkerLlm.Core;

// Der Übergang von 8078 auf 17401 (Auftrag, Abschnitt 4.3; CHANGELOG). Installierte StykkerHost haben „…:8078“
// gespeichert. Deshalb hört der Server auf dem Vorgabeport zusätzlich auf dem alten Port, aber dort nur für die beiden
// Wege der Model-Hosts (HostNameRule.IsModelHostPath). Jede andere Anfrage auf 8078 bekommt 410 mit dem Hinweis auf den
// neuen Port. Der Übergang entfällt in einer späteren Version.
public static class LegacyPort
{
    public const int Port = 8078;

    // Hört der Server zusätzlich auf 8078? Nur auf dem Vorgabeport: ein anderer Port (auch 8078 selbst als --port) bleibt
    // wie bisher. Die Oberfläche des Fensters startet den Server mit --port, also greift es auch dort.
    public static bool Listens(int webPort) => webPort == StykkerPorts.Llm;

    // Darf die Anfrage auf 8078 durch? Nur die Wege der Model-Hosts, die Methode zählt (POST für die Kopplung).
    public static bool Passes(string path, string method) => HostNameRule.IsModelHostPath(path, method);

    // Ist der Port frei? Belegt (zweiter Server, anderes Programm): der Server startet trotzdem, nur ohne den alten Port.
    public static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
