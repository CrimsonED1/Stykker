namespace StykkerLlm.Core;

// Beschreibung eines simulierten Servers (Simulator-Fenster). Nur Zahlen und Namen; nichts davon ist echt.
public sealed class SimServerSpec
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public BackendKind Kind { get; set; } = BackendKind.LlamaCpp;
    // Anzeigename (llama.cpp: --alias); Ollama und LM Studio zeigen ihren Programmnamen
    public string Name { get; set; } = "";
    public string Model { get; set; } = "qwen3-8b";
    // Kontext insgesamt (je Slot: Kontext / Slots)
    public int Context { get; set; } = 8192;
    public int Slots { get; set; } = 2;
    public double TpsMin { get; set; } = 30;
    public double TpsMax { get; set; } = 60;
    public double RequestsPerMinute { get; set; } = 6;
    // Anteil der erzeugten Token, die "Denken" sind (0 bis 100)
    public int ThinkPercent { get; set; }
    public bool ToolCalls { get; set; }
    // Größe der Modelldatei, bestimmt den simulierten VRAM-Bedarf
    public double ModelGb { get; set; } = 5;
    // llama.cpp-Art als Strata-Server: Python-Server mit Engine-Kindprozess, ein Slot, Live-Werte nur über /metrics (kein Log)
    public bool Strata { get; set; }
    // Spekulatives Decoding: Größe des Entwurfsmodells in Token (0 = aus, dann zählt der Server nichts)
    public int DraftModel { get; set; }
    // Aufbau für den GGUF-Kopf (SimGguf) und die Ansicht „Model“: Schichten, Parameter (Milliarden), Experten (0 = dicht),
    // davon je Token aktiv, aktive Parameter; ExtraArgs hängt an die Kommandozeile (z. B. „--n-cpu-moe 8“)
    public int Layers { get; set; } = 36;
    public double ParamsB { get; set; } = 8.2;
    public int Experts { get; set; }
    public int ExpertsUsed { get; set; }
    public double ActiveB { get; set; }
    public string ExtraArgs { get; set; } = "";

    public SimServerSpec Clone(bool keepId = false) => new()
    {
        Id = keepId ? Id : Guid.NewGuid(), Kind = Kind, Name = Name, Model = Model, Context = Context, Slots = Slots, TpsMin = TpsMin, TpsMax = TpsMax,
        RequestsPerMinute = RequestsPerMinute, ThinkPercent = ThinkPercent, ToolCalls = ToolCalls, ModelGb = ModelGb, Strata = Strata, DraftModel = DraftModel,
        Layers = Layers, ParamsB = ParamsB, Experts = Experts, ExpertsUsed = ExpertsUsed, ActiveB = ActiveB, ExtraArgs = ExtraArgs,
    };

    // Werte in sinnvolle Grenzen bringen (Eingabe aus Textfeldern)
    public void Normalize()
    {
        if (Kind != BackendKind.LlamaCpp) Strata = false;
        Slots = Strata ? 1 : Math.Clamp(Slots, 1, 16);
        Context = Math.Clamp(Context, 512 * Slots, 1_048_576);
        TpsMin = Math.Clamp(TpsMin, 0.5, 5000);
        TpsMax = Math.Clamp(TpsMax, TpsMin, 5000);
        RequestsPerMinute = Math.Clamp(RequestsPerMinute, 0, 600);
        ThinkPercent = Math.Clamp(ThinkPercent, 0, 95);
        ModelGb = Math.Clamp(ModelGb, 0.1, 400);
        if (string.IsNullOrWhiteSpace(Model)) Model = "model";
    }

    // Beispielzusammenstellung für den ersten Start des Simulators: zwei llama-server (einer mit Denken und Werkzeugaufrufen),
    // eine Ollama-Instanz und LM Studio mit eigener Engine
    public static List<SimServerSpec> Defaults() => new()
    {
        new SimServerSpec
        {
            Kind = BackendKind.LlamaCpp, Name = "qwen3-8b", Model = "qwen3-8b-q4_k_m", Context = 16384, Slots = 2, TpsMin = 55, TpsMax = 85,
            RequestsPerMinute = 22, ThinkPercent = 45, ToolCalls = true, ModelGb = 5.0,
        },
        // Ein Mixture-of-Experts-Modell wie Qwen3-30B-A3B: 128 Experten je Schicht, 8 davon je Token; die Experten der
        // ersten acht Schichten liegen auf dem Prozessor (--n-cpu-moe 8)
        new SimServerSpec
        {
            Kind = BackendKind.LlamaCpp, Name = "qwen3-30b-a3b", Model = "qwen3-30b-a3b-q4_k_m", Context = 8192, Slots = 1, TpsMin = 38, TpsMax = 62,
            RequestsPerMinute = 9, ThinkPercent = 30, ToolCalls = true, ModelGb = 17.3,
            Layers = 48, ParamsB = 30.5, Experts = 128, ExpertsUsed = 8, ActiveB = 3.3, ExtraArgs = "--n-cpu-moe 8",
        },
        new SimServerSpec { Kind = BackendKind.Ollama, Name = "Ollama", Model = "llama3.2:3b", Context = 4096, Slots = 1, ModelGb = 2.0 },
        new SimServerSpec
        {
            Kind = BackendKind.LmStudio, Name = "LM Studio", Model = "gemma-3-4b-it", Context = 8192, Slots = 2, TpsMin = 70, TpsMax = 110,
            RequestsPerMinute = 6, ThinkPercent = 20, ToolCalls = true, ModelGb = 2.6, Layers = 34, ParamsB = 3.9,
        },
    };
}
