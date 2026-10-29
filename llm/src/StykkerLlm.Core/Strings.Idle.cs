namespace StykkerLlm.Core;

// Leerlauf auf der Serverkarte (IdleInfo): wie lange ohne Anfrage, wann entladen wird – English
public static partial class Strings
{
    public static string CardIdle(string span) => $"idle {span}";
    public static string CardIdleAtLeast(string span) => $"idle ≥ {span}";
    public static string CardUnloadIn(string span) => $"unload in {span}";

    public const string CardIdleTip =
        "No request for this long. The model keeps its VRAM until you stop or unload it. Saved profiles can unload by themselves after some idle minutes (saved profile → “Unload when idle”).";
    public static string CardIdleUnloadTip(int minutes) =>
        $"No request for this long. The profile unloads the model after {minutes} min without a request, then the VRAM is free again.";
}
