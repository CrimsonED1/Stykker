using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// RAM-Typ, Takt und Bandbreite aus der SMBIOS-Tabelle (docs/plan-ui-redesign.md, U14)
[TestClass]
public class N64_SmbiosTests
{
    // Eine Typ-17-Struktur (Memory Device, SMBIOS 3.x, 0x28 Byte) mit zwei Texten dahinter
    private static byte[] Device(int sizeMb, byte type, int speed, int configured)
    {
        var d = new byte[0x28];
        d[0] = 17; d[1] = 0x28;
        BitConverter.GetBytes((ushort)(sizeMb >= 0x7FFF ? 0x7FFF : sizeMb)).CopyTo(d, 0x0C);
        d[0x12] = type;
        BitConverter.GetBytes((ushort)speed).CopyTo(d, 0x15);
        BitConverter.GetBytes((uint)sizeMb).CopyTo(d, 0x1C);
        BitConverter.GetBytes((ushort)configured).CopyTo(d, 0x20);
        return d.Concat("DIMM_A1\0Vendor\0\0"u8.ToArray()).ToArray();
    }

    private static byte[] Other(byte type) => new byte[] { type, 4, 0, 0, 0, 0 };   // fester Teil 4 Byte, keine Texte
    private static byte[] End() => new byte[] { 127, 4, 0, 0, 0, 0 };

    private static byte[] Raw(params byte[][] parts)
    {
        var table = parts.SelectMany(p => p).ToArray();
        var head = new byte[8];
        BitConverter.GetBytes(table.Length).CopyTo(head, 4);
        return head.Concat(table).ToArray();
    }

    [TestMethod]
    public void Ddr5_TwoModules_ConfiguredSpeed_TwoChannels()
    {
        var empty = Device(0, 0x22, 0, 0);   // leerer Steckplatz
        var m = Smbios.ParseRaw(Raw(Other(0), Device(32768, 0x22, 6400, 6000), empty, Device(32768, 0x22, 6400, 6000), End()))!;
        Assert.AreEqual("DDR5", m.Type);
        Assert.AreEqual(6000, m.SpeedMts, "eingestellter Takt, nicht der Höchsttakt");
        Assert.AreEqual(2, m.Modules);
        Assert.AreEqual(64, m.TotalGb, 0.01);
        Assert.AreEqual(2, m.Channels);
        Assert.AreEqual(96, m.BandwidthGbs, 0.01);
        Assert.AreEqual("DDR5-6000", m.Short);
    }

    [TestMethod]
    public void Ddr4_OneModule_OneChannel_NoTable_IsNull()
    {
        var m = Smbios.ParseRaw(Raw(Device(16384, 0x1A, 3200, 0), End()))!;
        Assert.AreEqual("DDR4", m.Type);
        Assert.AreEqual(3200, m.SpeedMts, "ohne eingestellten Takt gilt der Höchsttakt");
        Assert.AreEqual(1, m.Channels);
        Assert.AreEqual(25.6, m.BandwidthGbs, 0.01);
        Assert.IsNull(Smbios.ParseRaw(Raw(Other(4), End())));
        Assert.IsNull(Smbios.ParseRaw(new byte[3]));
    }

    [TestMethod]
    public void TypeCodes_AndUnknownSpeed()
    {
        Assert.AreEqual("DDR2", Smbios.TypeName(0x13));
        Assert.AreEqual("SDRAM", Smbios.TypeName(0x0F));
        Assert.AreEqual("RAM", Smbios.TypeName(0x19));
        // 0xFFFF ohne erweitertes Feld (Struktur zu kurz) ist „unbekannt“, nicht 65535 MT/s
        var m = Smbios.ParseRaw(Raw(Device(8192, 0x22, 0xFFFF, 0xFFFF), End()))!;
        Assert.AreEqual(0, m.SpeedMts);
        Assert.AreEqual("DDR5", m.Short);
    }
}

// Auf echtem Windows: die Firmware liefert eine Speichertabelle und die Kernzeiten (ohne Windows: inconclusive)
[TestClass]
public class N64_SmbiosWindowsTests
{
    [TestMethod]
    public void RealMachine_ReportsMemoryAndCores()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("nur Windows"); return; }
        var m = StykkerLlm.Platform.Windows.SysInfo.ReadMemory();
        if (m == null) { Assert.Inconclusive("keine SMBIOS-Speichertabelle (VM?)"); return; }
        Assert.IsTrue(m.Modules >= 1 && m.TotalGb > 0, m.ToString());
        Console.WriteLine($"{m.Short} · {m.Modules} modules · {m.TotalGb} GB · ≈{m.BandwidthGbs:0} GB/s");
        var sys = new StykkerLlm.Platform.Windows.SysInfo();
        sys.Read(); Thread.Sleep(300);
        var s = sys.Read()!;
        Assert.AreEqual(Environment.ProcessorCount, s.CoreLoads?.Length, "eine Auslastung je logischem Kern");
    }
}
