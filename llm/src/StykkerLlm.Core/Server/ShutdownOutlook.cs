namespace StykkerLlm.Core;

// Was passiert, wenn ein Fenster jetzt beendet wird (Rückfrage beim Schließen). Die Zahlen zählen die anderen Halter
// und Webseiten, das Fenster selbst nicht. Wie ShouldStop: der Server endet nur, wenn niemand sonst ihn braucht, keine
// Ausnahme greift (Keep the server running, --stay) und kein Modelltest oder Benchmark läuft.
public sealed record ShutdownOutlook(int OtherWindows, int OtherTerminals, int OtherPages, bool KeepRunning, bool Busy, int ProxyActive)
{
    public bool WouldStop => OtherWindows + OtherTerminals + OtherPages == 0 && !KeepRunning && !Busy;

    // Der Hinweis für die Rückfrage: was das Beenden mit dem Server macht. Läuft eine Anfrage über den Proxy, steht das
    // dabei; bricht sie ab, weil der Server endet, kommt zusätzlich eine Warnung.
    public ShutdownHint Hint()
    {
        string note;
        if (KeepRunning) note = Strings.OutlookKeptBySetting;
        else if (Busy) note = Strings.OutlookBusy;
        else if (!WouldStop) note = Strings.OutlookStillOpen(OpenList());
        else note = Strings.OutlookStops;

        string? warning = null;
        if (ProxyActive > 0)
        {
            if (WouldStop) warning = Strings.OutlookProxyCut;
            else note += " " + Strings.OutlookProxyRunning;
        }
        return new ShutdownHint(note, warning, ProxyActive);
    }

    private string OpenList()
    {
        var parts = new List<string>();
        if (OtherWindows > 0) parts.Add(Strings.OutlookWindows(OtherWindows));
        if (OtherTerminals > 0) parts.Add(Strings.OutlookTerminals(OtherTerminals));
        if (OtherPages > 0) parts.Add(Strings.OutlookPages(OtherPages));
        return string.Join(", ", parts);
    }
}

// Der Hinweis zur Rückfrage: Note ist der Satz zum Server, Warning (sonst null) warnt vor einem Abbruch,
// ProxyActive zählt die laufenden Anfragen über den Proxy (die Rückfrage fragt dann immer)
public sealed record ShutdownHint(string Note, string? Warning, int ProxyActive);
