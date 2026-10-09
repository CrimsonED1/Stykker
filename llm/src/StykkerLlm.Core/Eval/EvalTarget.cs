using System.Net;

namespace StykkerLlm.Core.Eval;

// Wer ist das Ziel eines Laufs? Für den Vergleich zweier Rechner braucht jeder Lauf die Maschine, auf der
// das **Modell** lief – nicht die, von der aus gestartet wurde. Ein Lauf gegen einen entfernten Server wird
// zwar von hier getrieben, das Modell rechnet aber dort; die Maschine ist deshalb der Host aus der URL.
// Bei einem lokalen Server (127.0.0.1) ist der Host dieser Rechner.
public sealed record EvalTargetInfo(string Machine, string Model, string ModelFile, string Settings, string Quant);

public static class EvalTarget
{
    public static string MachineOf(string baseUrl)
    {
        string host = "";
        try { host = new Uri(baseUrl.TrimEnd('/')).Host; } catch { /* keine URL: dann bleibt es leer */ }
        if (host.Length == 0) return Environment.MachineName;
        if (IsLocal(host)) return Environment.MachineName;
        return host;
    }

    private static bool IsLocal(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)) ||
        host.Equals("::1", StringComparison.Ordinal);

    // Angaben zum Ziel über /props (Modellpfad, ctx, Slots). Ein alter oder nicht erreichbarer Server
    // liefert nichts – dann bleiben die Felder leer, der Lauf funktioniert trotzdem.
    public static async Task<EvalTargetInfo> ProbeAsync(HttpClient http, string baseUrl, CancellationToken ct = default)
    {
        var machine = MachineOf(baseUrl);
        try
        {
            using var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/props", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new EvalTargetInfo(machine, "", "", "", "");
            if (LlamaProps.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is not { } p)
                return new EvalTargetInfo(machine, "", "", "", "");
            string file = "";
            if (!string.IsNullOrWhiteSpace(p.ModelPath))
            {
                try { file = Path.GetFileName(p.ModelPath); } catch { file = p.ModelPath; }
            }
            var ctx = p.NCtx > 0 ? $"ctx {p.NCtx} · slots {Math.Max(1, p.TotalSlots)}" : "";
            return new EvalTargetInfo(machine, p.ModelAlias ?? "", file, ctx, p.ModelFtype ?? "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new EvalTargetInfo(machine, "", "", "", ""); }
    }
}
