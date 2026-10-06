namespace StykkerLlm.Core;

// Spaltenköpfe der Listen in TUI und stykker-Befehlen. Die Fenster zeigen dieselben Wörter in ihren Karten,
// deshalb stehen sie hier und nicht in der Oberfläche, die sie druckt.
public static partial class Strings
{
    public const string ColId = "ID", ColPort = "Port", ColLastStarted = "Last started", ColTarget = "Target";
    public const string ColRep = "Rep", ColDone = "Done", ColCurrent = "Current", ColOn = "On";
    public const string ColKind = "Kind", ColDescription = "Description", ColVersion = "Ver", ColCategories = "Categories";
    public const string ColSizeGb = "Size GB", ColTasks = "Tasks";
    public const string RowTotal = "total";
    public const string NeverRun = "never";
    public const string BenchNoneYet = "No benchmarks yet. Run one with “stykker bench” or “/bench run”.";
    public const string RecordingNoneYet = "No recordings yet. Start one with “Record all” or “/record all”.";

    // Zeilen der Detailansicht (stykker show / /saved <name>)
    public const string RowFirstSeen = "First seen", RowRunningTime = "Running time", RowTpsBest = "Tokens/s best",
        RowTpsAverage = "Tokens/s average", RowGpuMax = "GPU memory max", RowArgument = "(argument)";
}