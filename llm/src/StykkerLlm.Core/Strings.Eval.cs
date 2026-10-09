using System.Globalization;

namespace StykkerLlm.Core;

// Texte der Modelltests für Web und TUI (Bereich 9 aus docs/ui.md). Die Seiten und Befehle zeigen an und
// rufen auf; was hier steht, steht in Fenster, Web und TUI gleich.
public static partial class Strings
{
    // Spalten und Bereiche
    public const string EvalQueueTitle = "Queue", EvalSuite = "Suite", EvalSuites = "Suites", EvalProgress = "Progress", EvalScore = "Score";
    public const string EvalTasksTitle = "Tasks";
    public const string EvalTotal = "Total", EvalRunsSaved = "runs saved", EvalTime = "Time", EvalSize = "Size", EvalSource = "Source";
    // Rechner, auf dem das Modell lief – damit dieselbe Modelldatei auf zwei Rechnern zwei Zeilen ergibt
    public const string EvalMachine = "Machine";
    public const string EvalTest = "Test", EvalTask = "Task", EvalAnswer = "Answer", EvalTaskMatrix = "Tasks · latest run per model";
    public const string EvalRanking = "Ranking", EvalReload = "↻ Reload", EvalModelsForTests = "Models for tests", EvalAddManual = "+ Add manually";
    public const string EvalPort = "Port", EvalProgram = "Program";

    // Lauf und Warteschlange
    public const string EvalNoModelsRuns = "No models yet – open Models and press “Find models”.";
    public const string EvalNoPython = "No Python found – coding tasks will be skipped.";
    public const string EvalNoResults = "No results yet.";
    public const string EvalNoAnswer = "(no answer text)";
    public const string EvalLive = "Live";
    public static string EvalJobs(int n) => $"{n} jobs";
    public static string EvalJobsLeft(int left, int all) => $"{left} of {all} left";
    public static string EvalModelsTimesSuites(int models, int suites) => $"{models} models × {suites} suites";
    public static string EvalTasksCount(int n) => $"{n} tasks";
    public static string EvalSuiteBuiltIn => " · built-in";
    public static string EvalRounds(int n) => $"{n} rounds";

    // Modelle
    public const string EvalLlamaServer = "llama-server used for found GGUF files";
    public const string EvalModelRoots = "Folders searched for GGUF files (one per line)";
    public const string EvalArgsHint = "Arguments (the port in the arguments must match the port column)";
    public static string EvalModelFile(string file) => $"Model file: {file}";
    public const string EvalSearching = "searching …", EvalNoNewModels = "no new models found";
    public static string EvalNewModels(int n) => $"{n} new models (found GGUF files start unchecked)";

    // Katalog: eigene Suiten bearbeiten
    public const string EvalNewSuiteName = "new-suite-name";
    public const string EvalCatalogHint =
        "Pick a suite and a test, or create your own suite. Built-in suites are read-only; your suites are saved as JSON in the data folder (";
    public const string EvalReadOnlyTest = "Test (read-only)", EvalEditTest = "Edit test";
    public const string EvalTitle2 = "Title", EvalCategory = "Category", EvalMaxTokens = "Max tokens", EvalPrompt = "Prompt", EvalCheck = "Check";
    public const string EvalExpectedContains = "Must contain (comma-separated)", EvalExpectedExact = "Exact answer (alternatives comma-separated)";
    public const string EvalExpectedNumber = "Expected number", EvalExpectedJson = "Required JSON keys (comma-separated)";
    public const string EvalExpectedOther = "Expected (not used for this check)";
    public const string EvalPattern = "Pattern (.NET regex, case-insensitive)";
    public const string EvalPythonChecks = "Python checks – the answer is in the variable answer, use assert";
    public const string EvalPythonTests = "Python tests run after the model's code, use assert";
    public const string EvalChooseName = "Choose a new, unused name.", EvalOwnSuiteDescription = "My own tests", EvalNewTaskTitle = "New test";
    public const string EvalSaved = "Saved.", EvalDeleted = "Deleted.";
    public static string EvalSuiteFolder(string dir) => EvalCatalogHint + dir + ")";

    // Karten und Markierungen der Ergebnismatrix (Muster der Tabelle)
    public const string EvalMarkSkip = "skip", EvalMarkErr = "err", EvalMarkPass = "✓", EvalMarkHalf = "½", EvalMarkFail = "✗", EvalMarkNone = "–";
    public static string EvalSeconds(double sec) => $"{sec.ToString("0", CultureInfo.InvariantCulture)} s";
    public static string EvalSeconds1(double sec) => $"{sec.ToString("0.0", CultureInfo.InvariantCulture)} s";

    public static string EvalQueued(string msg) => $"{msg} model(s) with the built-in suite";


}