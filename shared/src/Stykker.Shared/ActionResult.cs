namespace Stykker.Shared;

// Das Ergebnis einer Aktion. Jede Antwort sagt in Worten, was passiert ist – die Oberfläche zeigt den Text
// unverändert an (englisch, wie die ganze Oberfläche). Fehler sind Ergebnisse, keine Ausnahmen.
public sealed record ActionResult(bool Ok, string Message);
