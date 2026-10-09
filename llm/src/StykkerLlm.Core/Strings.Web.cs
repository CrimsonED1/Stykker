namespace StykkerLlm.Core;

// Texte der Web-Oberfläche: Rahmen (Kopfzeile, Navigation) und der Rückfrage-Dialog. Alles, was eine Seite anzeigt,
// kommt aus dieser Datei – dieselben Sätze stehen im Fenster und in der TUI.
public static partial class Strings
{
    // Navigation
    public const string NavMonitor = "Monitor", NavBenchmarks = "Benchmarks", NavRecordings = "Recordings";
    // Kopfzeile: wenige Hauptpunkte stehen sichtbar, der Rest gruppiert im ☰ (wie das ☰-Panel des Fensters)
    public const string NavEval = "Model tests", NavMore = "More", NavGroupEval = "EVAL";
    // Zurück/Vor: nur in der Fenster-Hülle sichtbar (ein Browser hat eigene Knöpfe)
    public const string BtnBack = "Back", BtnForward = "Forward";
    // Ansicht: automatisch nach Fensterbreite – oder fest kompakt/voll
    public const string ViewAuto = "Auto", ViewSmall = "Compact", ViewFull = "Full";

    // Rückfrage-Dialog (Start, Stopp, Geheimnis)
    public const string BtnYes = "Yes", BtnNo = "No";
    public const string SecretPlaceholder = "value (used once, never stored)";

    // Kopfzeile
    public static string RamFreeGb(double gb) => $"RAM {gb.ToString("0.0", Inv)} GB free";
    public static string GpuHeadline(double usedGb, double totalGb) => $"GPU {usedGb.ToString("0.0", Inv)} / {totalGb.ToString("0", Inv)} GB";
}