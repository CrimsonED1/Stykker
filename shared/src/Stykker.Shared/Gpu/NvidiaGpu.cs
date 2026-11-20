namespace Stykker.Shared.Gpu;

// Eine Messung einer NVIDIA-Grafikkarte über NVML (siehe Windows/Nvml.cs): Auslastung, Grafikspeicher, Leistung,
// Temperatur, Takte und die Gründe, aus denen die Karte gerade drosselt. Genauer als die Windows-Zähler: NVML nennt auch
// den gesamten Grafikspeicher. Gebraucht von StykkerLLM (Monitor) und der gemeinsamen Hardware-Seite (Stykker.Hardware).
public sealed record NvidiaGpu(string Name, double Util, double MemUsedGb, double MemTotalGb, double PowerW, double PowerLimitW,
    double TempC, double MemUtil, double GfxMhz, double GfxMaxMhz, double MemMhz, double MemMaxMhz, ulong ThrottleBits)
{
    public double MemFreeGb => MemTotalGb - MemUsedGb;

    // Drosselgründe als Text (Leerlauf und Anzeigetakt zählen nicht als Drosselung)
    public string ThrottleText()
    {
        var parts = new List<string>();
        if ((ThrottleBits & 0x4) != 0) parts.Add("Power");
        if ((ThrottleBits & 0x20) != 0) parts.Add("Thermal (SW)");
        if ((ThrottleBits & 0x40) != 0) parts.Add("Thermal (HW)");
        if ((ThrottleBits & 0x88) != 0) parts.Add("HW brake");   // 0x8 Hardware-Slowdown, 0x80 Power-Brake
        if (parts.Count == 0) return (ThrottleBits & 0x1) != 0 ? "Idle" : "None";
        return string.Join(", ", parts);
    }

    // Nur echte Begrenzungen (Power, Thermal, Hardware-Bremse); Sync-Boost, App-Takt und Anzeigetakt zählen nicht
    public bool Throttled => (ThrottleBits & ThrottleMask) != 0;
    private const ulong ThrottleMask = 0x4 | 0x8 | 0x20 | 0x40 | 0x80;
}
