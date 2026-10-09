namespace StykkerLlm.Core;

// Rückfragen an den Nutzer, ohne eine Oberfläche zu kennen. Die Oberfläche zeigt Dialoge; Tests antworten per Skript.
public interface IUserPrompt
{
    // Ein Geheimnis (API-Schlüssel, Token) abfragen, verdeckt eingegeben; null = abgebrochen. Der Wert wird nie gespeichert.
    Task<string?> AskSecretAsync(string title, string text);
    // Ja/Nein-Frage; warning = Warnsymbol, Standardantwort ist immer "Nein"
    Task<bool> ConfirmAsync(string title, string text, bool warning = false);
    // Hinweis ohne Rückfrage
    Task InformAsync(string title, string text, bool warning = false);
}

// Kleine Formatierungshelfer ohne Oberflächenbezug
public static class Fmt
{
    // 75 s -> "1 min 15 s", 4000 s -> "1 h 06 min"
    public static string Dur(double sec)
    {
        int s = (int)Math.Round(sec);
        return s >= 3600 ? $"{s / 3600} h {s / 60 % 60:00} min" : s >= 60 ? $"{s / 60} min {s % 60:00} s" : $"{s} s";
    }
}
