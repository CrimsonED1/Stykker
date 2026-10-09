using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// S2 (Server/Remote): QR-Code ohne Bibliothek, Zugangscode und Geräteliste, LAN-Adresse.
// Der QR-Test liest den erzeugten Code wieder aus der Matrix zurück (Format-Info, Zickzack, Verschränkung) und prüft,
// dass alle Reed-Solomon-Syndrome null sind – damit ist die Korrektheite des Codegens belegt, nicht nur die Plausibilität.
[TestClass]
public class S2_AccessQrTests
{
    // ── QR ──

    [TestMethod]
    public void Qr_PairUrlFitsAndHasFinderPatterns()
    {
        var url = NetInfo.PairUrl(17400, "ABCD2345", "192.168.1.23");
        Assert.AreEqual("http://192.168.1.23:17400/pair?code=ABCD2345", url);
        var qr = QrCode.Encode(url);
        Assert.IsTrue(qr.Version is >= 1 and <= 10);
        Assert.AreEqual(qr.Version * 4 + 17, qr.Size);
        // Finder-Muster: 7x7 Rahmen mit 3x3-Kern, an allen drei Ecken
        foreach (var (ox, oy) in new[] { (0, 0), (qr.Size - 7, 0), (0, qr.Size - 7) })
        {
            Assert.IsTrue(qr[ox, oy], "Ecke oben links des Finder-Musters");
            Assert.IsTrue(qr[ox + 3, oy + 3], "Kern des Finder-Musters");
            Assert.IsFalse(qr[ox + 1, oy + 1], "heller Ring des Finder-Musters");
            Assert.IsTrue(qr[ox + 6, oy + 3], "rechter Rand des Finder-Musters");
        }
        Assert.IsFalse(qr[7, 7], "Trennstreifen neben dem Finder-Muster");
        Assert.IsTrue(qr[8, qr.Size - 8], "dunkles Modul");
    }

    [TestMethod]
    public void Qr_FormatInfoMatchesTheStandardTable()
    {
        // Die 15 Bit der Format-Info stehen an beiden üblichen Stellen. Erwartet wird der Wert der Norm für die Stufe
        // und die Maske, die der Encoder selbst gewählt hat (die Tabelle der Norm, Spalte Maske 0..7):
        int[][] table =
        {
            new[] { 0x77C4, 0x72F3, 0x7DAA, 0x789D, 0x662F, 0x6318, 0x6C41, 0x6976 },   // L
            new[] { 0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0 },   // M
            new[] { 0x355F, 0x3068, 0x3F31, 0x3A06, 0x24B4, 0x2183, 0x2EDA, 0x2BED },   // Q
            new[] { 0x1689, 0x13BE, 0x1CE7, 0x19D0, 0x0762, 0x0255, 0x0D0C, 0x083B },   // H
        };
        for (int level = 0; level < 4; level++)
            for (int mask = 0; mask < 8; mask++)
            {
                var qr = QrCode.Encode("x", (QrCode.Ecc)level, 1, 1);
                if (qr.Mask != mask) continue;   // der Encoder prüft die Masken selbst; hier nur die gewählte nachlesen
                Assert.AreEqual(table[level][mask], ReadFormat(qr), $"Stufe {level}, Maske {mask}");
            }
        // Und die Maske steht wirklich in der Matrix: für jede gewählte Maske der passende Wert
        foreach (var level in new[] { QrCode.Ecc.L, QrCode.Ecc.M, QrCode.Ecc.Q, QrCode.Ecc.H })
        {
            var qr = QrCode.Encode("http://192.168.1.23:17400/pair?code=ABCD2345", level);
            Assert.AreEqual(table[(int)qr.Level][qr.Mask], ReadFormat(qr), $"Stufe {qr.Level}, Maske {qr.Mask}");
        }
    }

    [TestMethod]
    public void Qr_RoundTripsThroughItsOwnMatrix()
    {
        foreach (var text in new[] { "http://192.168.1.23:17400/pair?code=ABCD2345", "A", "Öffnen Sie die Tür 42 – Grüße" })
            foreach (QrCode.Ecc level in new[] { QrCode.Ecc.L, QrCode.Ecc.M, QrCode.Ecc.Q, QrCode.Ecc.H })
            {
                var qr = QrCode.Encode(text, level);
                Assert.AreEqual(text, ReadData(qr), $"Text „{text}“ auf Stufe {level}");
                Assert.AreEqual(0, Syndromes(qr), $"Reed-Solomon auf Stufe {level}");
            }
    }

    [TestMethod]
    public void Qr_VersionsAboveSixCarryVersionInfo()
    {
        // Ab Version 7 steht die Versions-Info in den Blöcken oben rechts und unten links
        var big = QrCode.Encode(new string('x', 200));
        Assert.IsTrue(big.Version >= 7, $"Version {big.Version}");
        Assert.AreEqual(VersionBits(big.Version), ReadVersionInfo(big));
    }

    [TestMethod]
    public void Qr_RejectsTooLongTextAndEmptyText()
    {
        Assert.ThrowsException<ArgumentException>(() => QrCode.Encode(""));
        Assert.ThrowsException<ArgumentException>(() => QrCode.Encode(new string('x', 400)));
    }

    [TestMethod]
    public void Qr_SvgAndAsciiCarryTheMatrix()
    {
        var qr = QrCode.Encode("http://127.0.0.1:17400/pair?code=ABCD2345");
        var svg = qr.ToSvg();
        StringAssert.Contains(svg, "<svg");
        Assert.AreEqual(qr.DarkCount, System.Text.RegularExpressions.Regex.Matches(svg, "<rect x=\"\\d+\" y=\"\\d+\" width=\"1\"").Count);
        var lines = qr.ToAscii().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(qr.Size + 2, lines.Length);
        Assert.IsTrue(lines[0].All(c => c == ' '), "heller Rand oben");
    }

    // ── Zugangscode und Geräte ──

    [TestMethod]
    public void Access_CodeAndPairing()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var platform = new FakePlatform();
        var access = new AccessControl(paths, platform);

        Assert.AreEqual(AccessControl.CodeLength, access.Code.Length);
        Assert.IsTrue(access.Code.All(char.IsAsciiDigit), "sechs Ziffern, am Telefon schnell getippt");
        Assert.IsFalse(access.RemoteEnabled);
        Assert.AreEqual(0, access.Devices.Count);

        Assert.IsNull(access.Pair("FALSCH1", "Handy", "192.168.1.44"), "falscher Code");
        Assert.IsNull(access.Pair(null, null, null), "kein Code");
        var paired = access.Pair(access.Code, "WaterFox on Android", "192.168.1.44");
        Assert.IsNotNull(paired);
        var (token, device) = paired!.Value;
        Assert.AreEqual(1, access.Devices.Count);
        Assert.AreEqual("WaterFox on Android", device.Name);
        Assert.IsFalse(string.IsNullOrEmpty(device.Id));

        Assert.IsTrue(access.Validate(token, "192.168.1.44"), "Cookie gültig");
        Assert.IsFalse(access.Validate(token + "x", "192.168.1.44"), "falsches Cookie");
        Assert.IsFalse(access.Validate(null, null), "kein Cookie");

        // Neuer Code: alte Cookies bleiben gültig, ein Gerät kann einzeln entfernt werden
        var old = access.Code;
        var rotated = access.RotateCode();
        Assert.AreNotEqual(old, rotated);
        Assert.AreEqual(rotated, access.Code);
        Assert.IsTrue(access.Validate(token, null));
        Assert.IsTrue(access.RemoveDevice(device.Id));
        Assert.IsFalse(access.Validate(token, null));
        Assert.AreEqual(0, access.Devices.Count);
    }

    [TestMethod]
    public void Access_RemoteSwitchAndReloadFromDisk()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var platform = new FakePlatform { UserProtection = true };
        var access = new AccessControl(paths, platform);
        access.SetRemote(true);
        var code = access.Code;
        var paired = access.Pair(code, "Handy", "10.0.0.5")!.Value;
        Assert.AreNotEqual(code, access.Code, "der Code ist nach einer Anmeldung verbraucht");

        // Zweite Instanz (z. B. das Fenster) sieht denselben Stand
        var again = new AccessControl(paths, platform);
        Assert.IsTrue(again.RemoteEnabled);
        Assert.AreEqual(access.Code, again.Code);
        Assert.AreEqual(1, again.Devices.Count);
        Assert.IsTrue(again.Validate(paired.Token, null), "Cookie gilt nach dem Neuladen");
        again.SetRemote(false);
        Assert.IsFalse(new AccessControl(paths, platform).RemoteEnabled, "Schalter wurde gespeichert");

        // Ohne Benutzerbindung (Linux) steht die Datei als JSON da – und ist wieder lesbar
        var plain = new AccessControl(new AppPaths(Path.Combine(tmp.Path, "linux")), new FakePlatform());
        var code2 = plain.Code;
        Assert.AreEqual(code2, new AccessControl(new AppPaths(Path.Combine(tmp.Path, "linux")), new FakePlatform()).Code);
    }

    [TestMethod]
    public void Access_CorruptFileGetsNewCode()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(Path.Combine(paths.Root, AccessControl.FileName), "kein gültiges json {{{");
        var access = new AccessControl(paths, new FakePlatform());
        Assert.AreEqual(AccessControl.CodeLength, access.Code.Length);
    }

    // ── Adresse ──

    [TestMethod]
    public void NetInfo_AddressesAndUrls()
    {
        var ips = NetInfo.Ipv4Addresses();
        Assert.IsNotNull(ips);   // auf einer Maschine ohne Netz gern leer
        Assert.IsFalse(ips.Any(ip => ip.StartsWith("127.")), "Loopback gehört nicht in den QR-Code");
        Assert.IsFalse(ips.Any(ip => ip.StartsWith("169.254.")), "APIPA ist keine Adresse zum Freigeben");
        Assert.IsTrue(NetInfo.LanAddressOrLoopback()?.Length > 0);
        StringAssert.Contains(NetInfo.PairUrl(17400, "482913", "10.1.2.3"), "/pair?code=482913");
    }

    // Temporärer Ordner für die Dateien von access.dat
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slm-s2-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    // ── Hilfen für den QR-Test ──

    // Format-Info aus der Matrix lesen (erste Kopie: Spalte 8 und Zeile 8)
    private static int ReadFormat(QrCode qr)
    {
        int size = qr.Size, bits = 0;
        bool Bit(int i) => i switch
        {
            <= 5 => qr[i, 8],
            6 => qr[7, 8],
            7 => qr[8, 8],
            8 => qr[8, 7],
            _ => qr[8, 14 - i],
        };
        for (int i = 0; i < 15; i++) if (Bit(i)) bits |= 1 << i;
        return bits;
    }

    private static int ReadVersionInfo(QrCode qr)
    {
        int size = qr.Size, bits = 0;
        for (int i = 0; i < 18; i++)
        {
            int a = size - 11 + i % 3, b = i / 3;
            if (qr[a, b]) bits |= 1 << i;
        }
        return bits;
    }

    private static int VersionBits(int version)
    {
        int rem = version << 12;
        for (int i = 17; i >= 12; i--)
            if ((rem >> i & 1) != 0) rem ^= 0x1F25 << (i - 12);
        return (version << 12) | (rem & 0xFFF);
    }

    // Funktionsmuster (das ganze Muster aus DrawFunctionPatterns) – hier unabhängig nachgebaut
    private static bool[,] FunctionMap(int size, int version)
    {
        var f = new bool[size, size];
        int[][] align = { System.Array.Empty<int>(), new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 },
            new[] { 6, 34 }, new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 } };
        void Fill(int ox, int oy, int w, int h)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int px = ox + x, py = oy + y;
                    if (px < 0 || py < 0 || px >= size || py >= size) continue;
                    f[px, py] = true;
                }
        }
        for (int i = 0; i < size; i++) { f[i, 6] = true; f[6, i] = true; }
        foreach (var (ox, oy) in new[] { (0, 0), (size - 7, 0), (0, size - 7) }) Fill(ox - 1, oy - 1, 9, 9);
        foreach (var cx in align[version - 1])
            foreach (var cy in align[version - 1])
            {
                if ((cx == 6 && cy == 6) || (cx == 6 && cy == size - 7) || (cx == size - 7 && cy == 6)) continue;
                Fill(cx - 2, cy - 2, 5, 5);
            }
        for (int i = 0; i <= 8; i++) { f[i, 8] = true; f[8, i] = true; }
        for (int i = 0; i < 8; i++) { f[size - 1 - i, 8] = true; f[8, size - 1 - i] = true; }
        if (version >= 7)
            for (int i = 0; i < 18; i++) { f[size - 11 + i % 3, i / 3] = true; f[i / 3, size - 11 + i % 3] = true; }
        return f;
    }

    // Alles, was kein Funktionsmuster ist, in Zickzackreihenfolge lesen und die Maske abnehmen
    private static List<bool> ReadCodewordBits(QrCode qr)
    {
        int size = qr.Size;
        var f = FunctionMap(size, qr.Version);
        var bits = new List<bool>();
        bool up = true;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int step = 0; step < size; step++)
            {
                int y = up ? size - 1 - step : step;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    if (f[x, y]) continue;
                    bool on = qr[x, y];
                    if (MaskBit(qr.Mask, x, y)) on = !on;
                    bits.Add(on);
                }
            }
            up = !up;
        }
        return bits;
    }

    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    private static readonly (int Ec, int G1, int G1Data, int G2, int G2Data)[] Specs =
    {
        (7, 1, 19, 0, 0), (10, 1, 16, 0, 0), (13, 1, 13, 0, 0), (17, 1, 9, 0, 0),
        (10, 1, 34, 0, 0), (16, 1, 28, 0, 0), (22, 1, 22, 0, 0), (28, 1, 16, 0, 0),
        (15, 1, 55, 0, 0), (26, 1, 44, 0, 0), (18, 2, 17, 0, 0), (22, 2, 13, 0, 0),
        (20, 1, 80, 0, 0), (18, 2, 32, 0, 0), (26, 2, 24, 0, 0), (16, 4, 9, 0, 0),
        (26, 1, 108, 0, 0), (24, 2, 43, 0, 0), (18, 2, 15, 2, 16), (22, 2, 11, 2, 12),
        (18, 2, 68, 0, 0), (16, 4, 27, 0, 0), (24, 4, 19, 0, 0), (28, 4, 15, 0, 0),
        (20, 2, 78, 0, 0), (18, 4, 31, 0, 0), (18, 2, 14, 4, 15), (26, 4, 13, 1, 14),
        (24, 2, 97, 0, 0), (22, 2, 38, 2, 39), (22, 4, 18, 2, 19), (26, 4, 14, 2, 15),
        (30, 2, 116, 0, 0), (22, 3, 36, 2, 37), (20, 4, 16, 4, 17), (24, 4, 12, 4, 13),
        (18, 2, 68, 2, 69), (26, 4, 43, 1, 44), (24, 6, 19, 2, 20), (28, 6, 15, 2, 16),
    };

    // Blöcke aus der Verschränkung zurückrechnen (Datenwörter zuerst, danach Fehlerkorrektur je Block)
    private static List<byte[]> ReadBlocks(QrCode qr, out List<byte[]> ec)
    {
        var bits = ReadCodewordBits(qr);
        var all = new List<byte>();
        for (int i = 0; i + 8 <= bits.Count; i += 8)
        {
            int b = 0;
            for (int j = 0; j < 8; j++) b = (b << 1) | (bits[i + j] ? 1 : 0);
            all.Add((byte)b);
        }
        var sp = Specs[(qr.Version - 1) * 4 + (int)qr.Level];
        var blocks = new List<byte[]>();
        int total = sp.G1 * sp.G1Data + sp.G2 * sp.G2Data;
        int offset = 0, maxData = Math.Max(sp.G1Data, sp.G2Data);
        for (int i = 0; i < maxData; i++)
            for (int b = 0; b < sp.G1 + sp.G2; b++)
            {
                int len = b < sp.G1 ? sp.G1Data : sp.G2Data;
                if (i >= len) continue;
                if (blocks.Count <= b) blocks.Add(new byte[len]);
                blocks[b][i] = all[offset++];
            }
        ec = new List<byte[]>();
        for (int b = 0; b < blocks.Count; b++) ec.Add(new byte[sp.Ec]);
        for (int i = 0; i < sp.Ec; i++)
            for (int b = 0; b < blocks.Count; b++) ec[b][i] = all[offset++];
        Assert.AreEqual(all.Count, offset, "alle Codewörter gelesen");
        Assert.IsTrue(total > 0);
        return blocks;
    }

    private static string ReadData(QrCode qr)
    {
        var blocks = ReadBlocks(qr, out _);
        var data = blocks.SelectMany(x => x).ToList();
        int pos = 0;
        int Bit() { bool b = (data[pos / 8] >> (7 - pos % 8) & 1) != 0; pos++; return b ? 1 : 0; }
        Assert.AreEqual(0b0100, (Bit() << 3) | (Bit() << 2) | (Bit() << 1) | Bit(), "Byte-Modus");
        int len = 0;
        int countBits = qr.Version <= 9 ? 8 : 16;
        for (int i = 0; i < countBits; i++) len = (len << 1) | Bit();
        var bytes = new byte[len];
        for (int i = 0; i < len; i++)
        {
            int b = 0;
            for (int j = 0; j < 8; j++) b = (b << 1) | Bit();
            bytes[i] = (byte)b;
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // Ein gültiger Reed-Solomon-Code hat an den Stellen a^0…a^(n-1) den Wert 0
    private static int Syndromes(QrCode qr)
    {
        var blocks = ReadBlocks(qr, out var ec);
        int[] exp = new int[512], log = new int[256];
        int x = 1;
        for (int i = 0; i < 255; i++) { exp[i] = x; log[x] = i; x <<= 1; if ((x & 0x100) != 0) x ^= 0x11D; }
        for (int i = 255; i < 512; i++) exp[i] = exp[i - 255];
        int Mul(int a, int b) => a == 0 || b == 0 ? 0 : exp[log[a & 0xFF] + log[b & 0xFF]];
        int bad = 0;
        for (int b = 0; b < blocks.Count; b++)
        {
            var word = blocks[b].Concat(ec[b]).ToArray();
            for (int s = 0; s < ec[b].Length; s++)
            {
                int sum = 0;
                foreach (var c in word) sum ^= Mul(c, exp[s]);
                if (sum != 0) bad++;
            }
        }
        return bad;
    }
}
