namespace StykkerLlm.Core;

// Was beim Schließen des Fensters passiert, wenn der Dialog nach einer gemerkten Wahl („Nicht mehr fragen“) entfallen
// würde. Läuft eine Anfrage über den Proxy, wird trotzdem gefragt: der Hinweis sagt dann, was mit ihr passiert.
public static class CloseRule
{
    // Dieselben Werte, die die Oberfläche im Datenordner speichert
    public const string Ask = "ask", Tray = "tray", Quit = "quit";

    public static string Decide(string remembered, ShutdownHint? hint)
    {
        if (hint is { ProxyActive: > 0 }) return Ask;
        return remembered switch
        {
            Tray => Tray,
            Quit => Quit,
            _ => Ask,
        };
    }
}
