using System.Runtime.InteropServices;
using StykkerLlm.Core;

namespace StykkerLlm.Platform.Windows;

// Typ, Takt und Module des Arbeitsspeichers aus der SMBIOS-Tabelle der Firmware (nur StykkerLLM braucht das).
// Prozessor- und RAM-Auslastung kommen aus Stykker.Shared (SystemCounters).
public static class SysInfo
{
    [DllImport("kernel32.dll")] private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    public static MemoryInfo? ReadMemory()
    {
        const uint rsmb = 0x52534D42;
        uint size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size == 0 || size > 1 << 20) return null;
        var buf = new byte[size];
        return GetSystemFirmwareTable(rsmb, 0, buf, size) == size ? Smbios.ParseRaw(buf) : null;
    }
}
