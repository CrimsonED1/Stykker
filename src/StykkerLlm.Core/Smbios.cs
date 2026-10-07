using System.Text;

namespace StykkerLlm.Core;

// Arbeitsspeicher laut Firmware (docs/plan-ui-redesign.md, U14): Typ, Takt, Module und eine theoretische Bandbreite.
// Bandbreite = MT/s × 8 Byte × Kanäle; die Kanalzahl kennt die Tabelle nicht – geschätzt aus der Zahl der Module
// (1 Modul = 1 Kanal, sonst 2, wie bei fast allen Desktop-Boards). Deshalb „≈“ und „theoretisch“ in der Anzeige.
public sealed record MemoryInfo(string Type, int SpeedMts, int Modules, double TotalGb, int Channels)
{
    public double BandwidthGbs => SpeedMts * 8.0 * Channels / 1000.0;
    public string Short => SpeedMts > 0 ? $"{Type}-{SpeedMts}" : Type;
}

public static class Smbios
{
    // Rohdaten von GetSystemFirmwareTable('RSMB'): 8 Byte Kopf (RawSMBIOSData), dann die Strukturen
    public static MemoryInfo? ParseRaw(byte[] raw)
    {
        if (raw.Length < 8) return null;
        int len = BitConverter.ToInt32(raw, 4);
        return ParseTable(raw.AsSpan(8, Math.Min(len, raw.Length - 8)).ToArray());
    }

    // Die Strukturen selbst: je Typ 17 (Memory Device) Größe, Typ und Takt; leere Steckplätze zählen nicht
    public static MemoryInfo? ParseTable(byte[] t)
    {
        var types = new List<byte>();
        var speeds = new List<int>();
        double totalGb = 0;
        int i = 0;
        while (i + 4 <= t.Length)
        {
            byte type = t[i], len = t[i + 1];
            if (len < 4 || i + len > t.Length) break;
            if (type == 17 && len >= 0x15)
            {
                int sizeMb = SizeMb(t, i, len);
                if (sizeMb > 0)
                {
                    totalGb += sizeMb / 1024.0;
                    types.Add(t[i + 0x12]);
                    int speed = len >= 0x22 ? BitConverter.ToUInt16(t, i + 0x20) : 0;          // eingestellter Takt (2.7+)
                    if (speed == 0 && len >= 0x17) speed = BitConverter.ToUInt16(t, i + 0x15);  // sonst Höchsttakt
                    if (speed > 0) speeds.Add(speed);
                }
            }
            if (type == 127) break;   // Ende der Tabelle
            // hinter dem festen Teil stehen Texte, abgeschlossen mit zwei Nullbytes
            int j = i + len;
            while (j + 1 < t.Length && !(t[j] == 0 && t[j + 1] == 0)) j++;
            i = j + 2;
        }
        if (types.Count == 0) return null;
        int modules = types.Count;
        var name = TypeName(types.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key);
        int mts = speeds.Count > 0 ? speeds.Min() : 0;
        return new MemoryInfo(name, mts, modules, Math.Round(totalGb, 1), modules >= 2 ? 2 : 1);
    }

    private static int SizeMb(byte[] t, int i, int len)
    {
        int size = BitConverter.ToUInt16(t, i + 0x0C);
        if (size == 0 || size == 0xFFFF) return 0;
        if (size == 0x7FFF && len >= 0x20) return (int)(BitConverter.ToUInt32(t, i + 0x1C) & 0x7FFFFFFF);   // erweiterte Größe in MB
        return (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size;   // Bit 15: Angabe in KB
    }

    public static string TypeName(byte code) => code switch
    {
        0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3", 0x1E => "LPDDR4",
        0x22 => "DDR5", 0x23 => "LPDDR5", 0x13 => "SDRAM", 0x12 => "DDR", 0x13 + 6 => "DDR2", _ => "RAM",
    };
}
