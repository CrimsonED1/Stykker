namespace StykkerLlm.Core;

// Grobe VRAM-Schätzung für einen Serverstart. Immer als "estimate" anzeigen: das gemessene Maximum aus dem Verlauf ist genauer.
public sealed record VramEstimateResult(double ModelGb, double KvCacheGb, double ComputeGb)
{
    public double TotalGb => ModelGb + KvCacheGb + ComputeGb;
}

public static class VramEstimate
{
    // Pauschaler Compute-Puffer (Zwischenergebnisse, Logits) für Standard-Batchgrößen
    public const double ComputeBufferGb = 0.6;
    private const double Gb = 1024.0 * 1024 * 1024;

    // Bytes je Element eines KV-Cache-Typs (Blockgröße / 32 Elemente)
    public static double BytesPerElement(string? cacheType) => (cacheType ?? "f16").ToLowerInvariant() switch
    {
        "f32" => 4.0,
        "f16" or "bf16" => 2.0,
        "q8_0" => 34.0 / 32,
        "q5_1" => 24.0 / 32,
        "q5_0" => 22.0 / 32,
        "q4_1" => 20.0 / 32,
        "q4_0" or "iq4_nl" => 18.0 / 32,
        _ => 2.0,
    };

    // ctx = gesamter Kontext (n_ctx, verteilt auf alle Slots). ngl null = alles auf die GPU.
    // KV-Cache nur für Schichten mit voller Attention: bei Hybridmodellen (full_attention_interval) jede n-te Schicht,
    // bei Modellen mit Liste der KV-Köpfe je Schicht nur Schichten mit Köpfen > 0.
    public static VramEstimateResult Estimate(GgufInfo gguf, int ctx, string? cacheTypeK, string? cacheTypeV, int? ngl = null)
    {
        double modelGb = (gguf.FileSize > 0 ? gguf.FileSize : 0) / Gb;
        int layers = gguf.BlockCount ?? 0;
        if (ngl is int n && layers > 0 && n < layers + 1) modelGb *= Math.Clamp((double)n / (layers + 1), 0, 1);

        double kvBytes = 0;
        if (layers > 0 && ctx > 0)
        {
            int heads = gguf.HeadCount ?? 0;
            int emb = gguf.EmbeddingLength ?? 0;
            long keyLen = gguf.KeyLength ?? (heads > 0 ? emb / heads : 0);
            long valLen = gguf.ValueLength ?? keyLen;
            var perLayer = gguf.HeadCountKvPerLayer;   // null: Metadaten fehlen
            double kvHeadsSum;
            if (perLayer is { Length: > 0 })
                kvHeadsSum = perLayer.Sum();
            else
            {
                long kvHeads = gguf.Int($"{gguf.Architecture}.attention.head_count_kv") ?? heads;
                int interval = gguf.FullAttentionInterval is int iv && iv > 0 ? iv : 1;
                kvHeadsSum = (double)kvHeads * (layers / interval);   // Anzahl Schichten mit voller Attention
            }
            kvBytes = ctx * kvHeadsSum * (keyLen * BytesPerElement(cacheTypeK) + valLen * BytesPerElement(cacheTypeV));
        }
        return new VramEstimateResult(modelGb, kvBytes / Gb, ComputeBufferGb);
    }

    // CPU-seitiger RAM-Bedarf beim Start: Gewichte und KV der CPU-Schichten (Anteil 1 - ngl-Anteil) plus Compute-Puffer,
    // sobald überhaupt etwas auf der CPU liegt. ngl null = alles auf der GPU (wie Estimate) -> 0.
    public static double CpuRamGb(GgufInfo gguf, int ctx, string? cacheTypeK, string? cacheTypeV, int? ngl = null)
    {
        int layers = gguf.BlockCount ?? 0;
        int n = ngl ?? (layers > 0 ? layers + 1 : 0);
        double frac = layers > 0 ? Math.Clamp((double)n / (layers + 1), 0, 1) : 1.0;
        if (frac >= 1) return 0;
        double modelGb = (gguf.FileSize > 0 ? gguf.FileSize : 0) / Gb;
        double kvGb = Estimate(gguf, ctx, cacheTypeK, cacheTypeV, ngl).KvCacheGb;
        return (modelGb + kvGb) * (1 - frac) + ComputeBufferGb;
    }
}
