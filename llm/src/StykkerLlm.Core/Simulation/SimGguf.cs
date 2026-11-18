using System.Text;

namespace StykkerLlm.Core;

// Kleine GGUF-Köpfe für die simulierten llama-server: Metadaten und Tensor-Verzeichnis wie bei einem echten Modell, aber
// keine Gewichte (die Datei hat ein paar Kilobyte). Damit zeigt die Ansicht „Model“ im Simulator Schichten und Experten.
public static class SimGguf
{
    // Schreibt die Datei, wenn sie fehlt; liefert den Pfad
    public static string Ensure(string dir, SimServerSpec spec)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, spec.Model + ".gguf");
        if (!File.Exists(path)) File.WriteAllBytes(path, Build(spec));
        return path;
    }

    // Parameter so verteilt, dass Gesamt- und aktive Parameter zum Vorbild passen (spec.ParamsB, spec.ActiveB)
    public static byte[] Build(SimServerSpec spec)
    {
        const string arch = "sim";
        int layers = Math.Max(1, spec.Layers);
        bool moe = spec.Experts > 1 && spec.ExpertsUsed > 0;
        double total = spec.ParamsB * 1e9;
        // MoE: aktiv = dicht + Experten·k/n und gesamt = dicht + Experten → Experten = (gesamt − aktiv) / (1 − k/n)
        double share = moe ? (double)spec.ExpertsUsed / spec.Experts : 0;
        double experts = moe ? Math.Max(0, (total - spec.ActiveB * 1e9) / (1 - share)) : 0;
        double dense = total - experts;

        var kv = new List<Action<BinaryWriter>>();
        void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        void Text(string k, string v) => kv.Add(w => { Str(w, k); w.Write(8u); Str(w, v); });
        void U32(string k, uint v) => kv.Add(w => { Str(w, k); w.Write(4u); w.Write(v); });
        Text("general.architecture", arch);
        Text("general.name", spec.Name.Length > 0 ? spec.Name : spec.Model);
        U32("general.file_type", 15);   // Q4_K_M
        U32($"{arch}.block_count", (uint)layers);
        U32($"{arch}.context_length", 40960);
        U32($"{arch}.embedding_length", 2048);
        if (moe)
        {
            U32($"{arch}.expert_count", (uint)spec.Experts);
            U32($"{arch}.expert_used_count", (uint)spec.ExpertsUsed);
        }

        var tensors = new List<(string Name, long Elems)> { ("token_embd.weight", (long)(dense * 0.08)), ("output.weight", (long)(dense * 0.08)) };
        long perLayerDense = (long)(dense * 0.84 / layers), perLayerExperts = (long)(experts / layers);
        for (int i = 0; i < layers; i++)
        {
            tensors.Add(($"blk.{i}.attn_q.weight", perLayerDense / 2));
            if (moe)
            {
                tensors.Add(($"blk.{i}.attn_output.weight", perLayerDense / 2));
                foreach (var t in new[] { "gate", "up", "down" }) tensors.Add(($"blk.{i}.ffn_{t}_exps.weight", perLayerExperts / 3));
            }
            else tensors.Add(($"blk.{i}.ffn_up.weight", perLayerDense / 2));
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
        bw.Write(0x46554747u); bw.Write(3u); bw.Write((ulong)tensors.Count); bw.Write((ulong)kv.Count);
        foreach (var k in kv) k(bw);
        ulong off = 0;
        foreach (var (name, elems) in tensors)
        {
            Str(bw, name); bw.Write(1u); bw.Write((ulong)Math.Max(1, elems)); bw.Write(12u); bw.Write(off); off += 4096;
        }
        bw.Flush();
        return ms.ToArray();
    }
}
