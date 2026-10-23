namespace StykkerLlm.Core;

// Fortschritt beim Lesen des Prompts (Prefill) eines Slots, für Karte und Tooltip. Neu zu lesen ist der Prompt ohne den
// Teil aus dem Prompt-Cache; Tps ist die Lesegeschwindigkeit dieser Anfrage (0 = noch unbekannt).
public sealed record PromptReading(int Done, int ToRead, int Cached, double Tps)
{
    public double Frac => ToRead > 0 ? Math.Clamp((double)Done / ToRead, 0, 1) : 0;

    // Restzeit in Sekunden, null solange die Geschwindigkeit unbekannt ist
    public double? SecondsLeft => Tps > 0 && ToRead > Done ? (ToRead - Done) / Tps : null;

    // Nur für einen Slot, der gerade liest, und nur wenn der Server den Cache meldet (llama.cpp: n_prompt_tokens_cache)
    public static PromptReading? Of(SlotView v) =>
        !v.Busy || !v.ReadingPrompt || v.PromptCached < 0 || v.CtxUsed <= 0 ? null
        : new PromptReading(v.PromptDone, Math.Max(0, v.CtxUsed - v.PromptCached), v.PromptCached, v.PromptTps);
}
