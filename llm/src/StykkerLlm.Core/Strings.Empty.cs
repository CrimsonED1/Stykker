namespace StykkerLlm.Core;

// Leere Seiten mit dem nächsten Schritt (Komponente EmptyState) – English
public static partial class Strings
{
    public const string EmptyResultsTitle = "No model tests yet",
        EmptyResultsText = "Model tests send a fixed set of tasks to your models and rank them by score. Three steps:",
        EmptyResultsStep1 = "Models: press “Find models” – it reads your saved profiles and the model folders.",
        EmptyResultsStep2 = "Runs: tick the models and suites you want, then “Add to queue”.",
        EmptyResultsStep3 = "Start the queue. Each model is started, tested and stopped again; the ranking appears here.",
        EmptyOpenModels = "Open Models", EmptyOpenRuns = "Open Runs",
        EmptyModelsTitle = "No models to test yet",
        EmptyRunsModelsTitle = "No models to pick from",
        EmptyBenchTitle = "No benchmark yet",
        EmptyBenchText = "Pick a running server or a saved profile above and press “Run benchmark”. Results stay here, can be repeated with the same prompts and compared side by side.",
        EmptyRecTitle = "No recordings yet",
        EmptyRecText = "A recording keeps t/s, VRAM, power and every request of a server for later. Start one with the record button on a server card or with “● Record all” above; stop it when you are done.",
        EmptyOpenMonitor = "Open the monitor",
        EmptyHostsTitle = "No model host paired yet",
        EmptyHostsText = "A model host is another PC that only runs models. Install StykkerHost there and enter the code shown above; its servers then appear here and on the monitor.",
        RecRunningTitle = "Recording now";
}
