namespace StykkerLlm.Core;

// Ein Programm, das über den Stykker-Proxy mit lokalen und Cloud-Modellen spricht (heute Claude Desktop und Claude Code).
// Kopfleiste, Client-Dialog und die Aktionen client.* kennen nur diese Schnittstelle. Ein weiterer Client bekommt eine
// eigene Klasse und einen Eintrag in ProxyClients.
public interface IProxyClient
{
    // Kennung für Aktionen und Adressen („claude“)
    string Id { get; }
    // Name für Oberfläche und Log („Claude“)
    string Title { get; }
    // Symbol aus dem Sprite („claude“)
    string IconName { get; }
    // Auf diesem PC benutzbar (nur dann erscheint der Chip in der Kopfleiste)
    bool Supported { get; }
    // Der Schalter „über den Stykker-Proxy“ ist an
    bool Enabled { get; }
    // Hinweis neben dem Schalter (z. B. dass die App die Einstellung erst beim Start liest)
    string SwitchHint { get; }
    bool Running { get; }
    int RunningCount { get; }
    string Enable(out string? error);
    string Disable(out string? error);
    int CloseRunning(out string? error);
    // Die erkannten Startvarianten. Jeder Abruf erkennt neu und protokolliert das (also nicht in jedem Takt abrufen).
    IReadOnlyList<ClientInstall> Installs { get; }
    // Startet eine Variante aus Installs. Meldung bei Erfolg; bei Fehlschlag error gesetzt.
    string Launch(ClientInstall install, out string? error);
}

public enum ClientInstallKind { Desktop, Cli }

// Eine Startvariante. Key: Kennung für die Aktion client.launch („desktop“, „desktop-exe“, „cli-proxy“, „cli-direct“).
// Path: Ordner des Store-Pakets, Programmdatei oder CLI-Datei – wird der Oberfläche und dem Log nie gezeigt.
// Available=false: nicht gefunden, Path ist dann null.
public sealed record ClientInstall(string Key, ClientInstallKind Kind, string Label, string? Path, bool Available);
