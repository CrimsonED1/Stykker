namespace StykkerLlm.Core;

// Spaltenköpfe der Listen in TUI und stykker-Befehlen. Die Fenster zeigen dieselben Wörter in ihren Karten,
// deshalb stehen sie hier und nicht in der Oberfläche, die sie druckt.
public static partial class Strings
{
    public const string ColPort = "Port";

    // Zeilen der Detailansicht (stykker show / /saved <name>)
    public const string RowFirstSeen = "First seen", RowRunningTime = "Running time", RowTpsBest = "Tokens/s best",
        RowTpsAverage = "Tokens/s average", RowGpuMax = "GPU memory max", RowArgument = "(argument)";
}