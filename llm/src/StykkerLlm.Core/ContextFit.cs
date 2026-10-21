namespace StykkerLlm.Core;

// Wie viel Kontext ein llama.cpp-Profil auf die Grafikkarte bekommt: freier VRAM minus Modell (auf der GPU) und Compute-Puffer,
// geteilt durch den KV-Cache je Token (aus dem GGUF-Kopf: Schichten, KV-Köpfe, Kopfgröße, Cache-Typ). Nur Anzeige, keine Startsperre.
public static class ContextFit
{
    // Abgerundet wird auf volle 1k Token
    public const int Step = 1024;

    // KV-Cache je Token in Byte (Schätzung über VramEstimate, also dieselbe Rechnung wie der Start-VRAM); null bei fehlenden Werten
    public static double? KvBytesPerToken(GgufInfo g, string? cacheTypeK, string? cacheTypeV)
    {
        if (g.BlockCount is not > 0) return null;
        double gb = VramEstimate.Estimate(g, Step, cacheTypeK, cacheTypeV).KvCacheGb;
        return gb > 0 ? gb * 1024.0 * 1024 * 1024 / Step : null;
    }

    // Größtes Kontextfenster in Token, das in den freien VRAM passt (auf 1k abgerundet, höchstens das Modell-Maximum).
    // 0 = keins passt (kein freier VRAM, oder das Modell allein passt nicht). null = GGUF-Werte fehlen, dann nichts anzeigen.
    public static int? MaxCtx(GgufInfo g, double freeVramGb, int? ngl = null, string? cacheTypeK = null, string? cacheTypeV = null)
    {
        if (g.FileSize <= 0) return null;
        var perToken = KvBytesPerToken(g, cacheTypeK, cacheTypeV);
        if (perToken is not > 0) return null;
        var model = VramEstimate.Estimate(g, Step, cacheTypeK, cacheTypeV, ngl);
        double room = freeVramGb - model.ModelGb - VramEstimate.ComputeBufferGb;
        if (freeVramGb <= 0 || room <= 0) return 0;

        double tokens = room * 1024.0 * 1024 * 1024 / perToken.Value;
        if (g.ContextLength is > 0 and var max) tokens = Math.Min(tokens, max);
        tokens = Math.Min(tokens, 10_000_000);
        return (int)(Math.Floor(tokens / Step) * Step);
    }
}
