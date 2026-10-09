namespace StykkerLlm.Core;

// QR-Code im Byte-Modus ohne fremde Bibliothek: das Fenster zeichnet die Module selbst, das Web bekommt SVG, die TUI
// Blockzeichen. Reicht für Adressen wie http://192.168.1.23:8078/pair?code=ABCD2345 (Version 1-10, Stufe M).
// Aufbau nach ISO/IEC 18004: Reed-Solomon über GF(256), Verschränkung der Blöcke, Zickzack-Platzierung, acht Masken.
public sealed class QrCode
{
    public enum Ecc { L = 0, M = 1, Q = 2, H = 3 }

    // Je Version und Stufe: Fehlerkorrektur-Wörter je Block, Blöcke der Gruppe 1 mit Datenwörtern, Blöcke der Gruppe 2 damit.
    // Datenwörter + Fehlerkorrektur-Wörter ergeben die Zeilensumme (Tabelle 9 der Norm).
    private readonly record struct BlockSpec(int Ec, int G1Blocks, int G1Data, int G2Blocks, int G2Data);

    private static readonly BlockSpec[] Specs =
    {
        new(7, 1, 19, 0, 0),   new(10, 1, 16, 0, 0),  new(13, 1, 13, 0, 0),  new(17, 1, 9, 0, 0),    // Version 1
        new(10, 1, 34, 0, 0),   new(16, 1, 28, 0, 0),  new(22, 1, 22, 0, 0),  new(28, 1, 16, 0, 0),   // 2
        new(15, 1, 55, 0, 0),   new(26, 1, 44, 0, 0),  new(18, 2, 17, 0, 0),  new(22, 2, 13, 0, 0),   // 3
        new(20, 1, 80, 0, 0),   new(18, 2, 32, 0, 0),  new(26, 2, 24, 0, 0),  new(16, 4, 9, 0, 0),    // 4
        new(26, 1, 108, 0, 0),  new(24, 2, 43, 0, 0),  new(18, 2, 15, 2, 16), new(22, 2, 11, 2, 12),  // 5
        new(18, 2, 68, 0, 0),   new(16, 4, 27, 0, 0),  new(24, 4, 19, 0, 0),  new(28, 4, 15, 0, 0),   // 6
        new(20, 2, 78, 0, 0),   new(18, 4, 31, 0, 0),  new(18, 2, 14, 4, 15), new(26, 4, 13, 1, 14),  // 7
        new(24, 2, 97, 0, 0),   new(22, 2, 38, 2, 39), new(22, 4, 18, 2, 19), new(26, 4, 14, 2, 15),  // 8
        new(30, 2, 116, 0, 0),  new(22, 3, 36, 2, 37), new(20, 4, 16, 4, 17), new(24, 4, 12, 4, 13),  // 9
        new(18, 2, 68, 2, 69),  new(26, 4, 43, 1, 44), new(24, 6, 19, 2, 20), new(28, 6, 15, 2, 16),  // 10
    };

    // Zeilen der Matrix je Version: 21, 25, 29 … 57
    private static readonly int[] VersionSizes = { 21, 25, 29, 33, 37, 41, 45, 49, 53, 57 };

    // Mittelpunkte der Ausrichtungs-Muster (Version 1 hat keine)
    private static readonly int[][] Align =
    {
        System.Array.Empty<int>(), new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 },
        new[] { 6, 34 }, new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };

    public int Version { get; }
    public int Size => VersionSizes[Version - 1];
    public Ecc Level { get; }
    public int Mask { get; }
    private readonly bool[] _dark;   // y * Size + x
    private readonly bool[] _function = Array.Empty<bool>();   // von den Mustern belegte Module (Rest = Daten)

    private QrCode(int version, Ecc level, int mask) { Version = version; Level = level; Mask = mask; _dark = new bool[Size * Size]; }
    private QrCode(int version, Ecc level, int mask, bool[] dark, bool[] function) { Version = version; Level = level; Mask = mask; _dark = dark; _function = function; }

    public bool this[int x, int y] => _dark[y * Size + x];
    public bool IsDark(int x, int y) => _dark[y * Size + x];
    public int DarkCount => _dark.Count(x => x);
    // Wie viele Module die Muster belegen (Rest = Datenmodule); die Norm rechnet 441 - 208 = 233 für Version 1
    public int FunctionCount => _function.Length == 0 ? 0 : _function.Count(x => x);
    public int DataCount => Size * Size - FunctionCount;

    public static QrCode Encode(string text, Ecc level = Ecc.M, int minVersion = 1, int maxVersion = 10)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("nothing to encode", nameof(text));
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        int version = 0;
        for (int v = Math.Max(1, minVersion); v <= Math.Min(maxVersion, 10) && version == 0; v++)
        {
            var spec = Specs[(v - 1) * 4 + (int)level];
            int capacity = spec.G1Blocks * spec.G1Data + spec.G2Blocks * spec.G2Data;
            int countBits = v <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 <= capacity * 8) version = v;
        }
        if (version == 0) throw new ArgumentException($"text too long for QR version {maxVersion} at level {level}", nameof(text));

        var sp = Specs[(version - 1) * 4 + (int)level];
        int dataCodewords = sp.G1Blocks * sp.G1Data + sp.G2Blocks * sp.G2Data;

        // ── Bitstrom: Modus, Zeichenlänge, Daten, Ende, Füllbytes ──
        var bits = new List<bool>();
        void Put(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add((value >> i & 1) != 0); }
        Put(4, 4);
        Put(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data) Put(b, 8);
        int capBits = dataCodewords * 8;
        for (int i = 0; i < 4 && bits.Count < capBits; i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);
        var stream = new byte[dataCodewords];
        for (int i = 0; i + 8 <= bits.Count; i += 8)
        {
            int b = 0;
            for (int j = 0; j < 8; j++) b = (b << 1) | (bits[i + j] ? 1 : 0);
            stream[i / 8] = (byte)b;
        }
        byte[] pads = { 0xEC, 0x11 };
        for (int i = bits.Count / 8, k = 0; i < dataCodewords; i++, k++) stream[i] = pads[k % 2];

        // ── Blöcke bilden, Fehlerkorrektur je Block, dann verschränken ──
        var blocks = new List<byte[]>();
        int offset = 0;
        for (int b = 0; b < sp.G1Blocks; b++) { blocks.Add(stream[offset..(offset + sp.G1Data)]); offset += sp.G1Data; }
        for (int b = 0; b < sp.G2Blocks; b++) { blocks.Add(stream[offset..(offset + sp.G2Data)]); offset += sp.G2Data; }
        var ecBlocks = new List<byte[]>();
        foreach (var block in blocks) ecBlocks.Add(ErrorCorrection(block, sp.Ec));
        var all = new List<byte>(dataCodewords + sp.Ec * blocks.Count);
        for (int i = 0; i < blocks.Max(x => x.Length); i++)
            for (int b = 0; b < blocks.Count; b++)
                if (i < blocks[b].Length) all.Add(blocks[b][i]);
        for (int i = 0; i < sp.Ec; i++)
            for (int b = 0; b < ecBlocks.Count; b++) all.Add(ecBlocks[b][i]);

        // ── Matrix: Funktionsmuster, dann die Daten mit jeder der acht Masken ──
        var work = new QrCode(version, level, 0);
        var reserved = new bool[work.Size * work.Size];
        work.DrawFunctionPatterns(reserved);
        int[] positions = work.DataPositions(reserved);
        // Die Matrix hat ein paar Restbits mehr (7 bei Version 2-6, 0 bei 7-13, 3 ab 14); die bleiben ungenutzt
        if (all.Count * 8 > positions.Length) throw new InvalidOperationException($"{all.Count * 8} codeword bits for {positions.Length} data modules");

        int bestMask = 0, bestScore = int.MaxValue;
        bool[] best = Array.Empty<bool>();
        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[])work._dark.Clone();
            int bit = 0;
            foreach (var p in positions)
            {
                bool on = bit / 8 < all.Count && (all[bit / 8] >> (7 - bit % 8) & 1) != 0;
                if (MaskBit(mask, p % work.Size, p / work.Size)) on = !on;
                candidate[p] = on;
                bit++;
            }
            DrawFormat(candidate, work.Size, level, mask);
            int score = Penalty(candidate, work.Size);
            if (score < bestScore) { bestScore = score; bestMask = mask; best = candidate; }
        }
        return new QrCode(version, level, bestMask, best, reserved);
    }

    // ── Funktionsmuster ──

    private void DrawFunctionPatterns(bool[] reserved)
    {
        int size = Size;
        // Taktmuster zuerst, damit die Finder-Muster es danach überschreiben können
        for (int i = 0; i < size; i++) { Set(i, 6, i % 2 == 0, reserved); Set(6, i, i % 2 == 0, reserved); }
        // Finder-Muster mit je einem hellen Rand (Trennstreifen)
        foreach (var (ox, oy) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
            for (int y = -1; y <= 7; y++)
                for (int x = -1; x <= 7; x++)
                {
                    int px = ox + x, py = oy + y;
                    if (px < 0 || py < 0 || px >= size || py >= size) continue;
                    bool on = x >= 0 && x <= 6 && y >= 0 && y <= 6 &&
                              (x is 0 or 6 || y is 0 or 6 || (x >= 2 && x <= 4 && y >= 2 && y <= 4));
                    Set(px, py, on, reserved);
                }
        // Ausrichtungs-Muster (5x5) – nicht dort, wo ein Finder-Muster sitzt
        foreach (var cx in Align[Version - 1])
            foreach (var cy in Align[Version - 1])
            {
                if ((cx == 6 && cy == 6) || (cx == 6 && cy == size - 7) || (cx == size - 7 && cy == 6)) continue;
                for (int y = -2; y <= 2; y++)
                    for (int x = -2; x <= 2; x++)
                        Set(cx + x, cy + y, Math.Max(Math.Abs(x), Math.Abs(y)) != 1, reserved);
            }
        // Dunkles Modul und die beiden Format-Bereiche als reserviert markieren
        Set(8, size - 8, true, reserved);
        for (int i = 0; i <= 8; i++) { if (!reserved[8 * size + i]) Set(i, 8, false, reserved); if (!reserved[i * size + 8]) Set(8, i, false, reserved); }
        for (int i = 0; i < 8; i++)
        {
            if (!reserved[8 * size + size - 1 - i]) Set(size - 1 - i, 8, false, reserved);
            if (!reserved[(size - 1 - i) * size + 8]) Set(8, size - 1 - i, false, reserved);
        }
        // Versions-Info (ab Version 7, 18 Bit mit Prüfsumme)
        if (Version >= 7)
        {
            int info = VersionInfo(Version);
            for (int i = 0; i < 18; i++)
            {
                bool on = (info >> i & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(a, b, on, reserved);
                Set(b, a, on, reserved);
            }
        }
    }

    private void Set(int x, int y, bool on, bool[] reserved)
    {
        _dark[y * Size + x] = on;
        reserved[y * Size + x] = true;
    }

    // Zickzack von rechts unten nach links oben; die Spalte 6 (Taktmuster) wird übersprungen
    private int[] DataPositions(bool[] reserved)
    {
        int size = Size;
        var list = new List<int>(size * size);
        bool up = true;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int step = 0; step < size; step++)
            {
                int y = up ? size - 1 - step : step;
                foreach (var col in new[] { right, right - 1 })
                    if (!reserved[y * size + col]) list.Add(y * size + col);
            }
            up = !up;
        }
        return list.ToArray();
    }

    // Format-Info an beide Stellen; Bit 0 ist das niedrigste Bit
    private static void DrawFormat(bool[] dark, int size, Ecc level, int mask)
    {
        int bits = FormatInfo(level, mask);
        bool Bit(int i) => (bits >> i & 1) != 0;
        for (int i = 0; i <= 5; i++) dark[8 * size + i] = Bit(i);
        dark[8 * size + 7] = Bit(6);
        dark[8 * size + 8] = Bit(7);
        dark[7 * size + 8] = Bit(8);
        for (int i = 9; i < 15; i++) dark[(14 - i) * size + 8] = Bit(i);
        for (int i = 0; i < 8; i++) dark[8 * size + size - 1 - i] = Bit(i);
        for (int i = 8; i < 15; i++) dark[(size - 15 + i) * size + 8] = Bit(i);
        dark[(size - 8) * size + 8] = true;
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

    // Vier Regeln der Norm: gleiche Farben in einer Reihe (3 + je weiteres Modul), 1:1:3:1:1, 2x2-Blöcke, Hälftenanteil
    private static int Penalty(bool[] dark, int size)
    {
        int score = 0;
        for (int dir = 0; dir < 2; dir++)
            for (int line = 0; line < size; line++)
            {
                int run = 0; bool? last = null;
                for (int i = 0; i < size; i++)
                {
                    int p = dir == 0 ? line * size + i : i * size + line;
                    bool on = dark[p];
                    if (last == on) run++;
                    else { score += 3 + (run >= 5 ? run - 5 : 0); run = 1; last = on; }
                    // 1:1:3:1:1: dunkel hell dunkel dunkel dunkel hell dunkel, mit je vier hellen Modulen davor/dahinter
                    if (i + 6 < size)
                    {
                        bool b0 = At(dark, size, dir, line, i), b1 = At(dark, size, dir, line, i + 1), b2 = At(dark, size, dir, line, i + 2),
                             b3 = At(dark, size, dir, line, i + 3), b4 = At(dark, size, dir, line, i + 4), b5 = At(dark, size, dir, line, i + 5),
                             b6 = At(dark, size, dir, line, i + 6);
                        if (b0 && !b1 && b2 && b3 && b4 && !b5 && b6)
                        {
                            bool before = true, after = true;
                            for (int k = 1; k <= 4; k++)
                            {
                                if (i - k >= 0 && At(dark, size, dir, line, i - k)) before = false;
                                if (i + 6 + k < size && At(dark, size, dir, line, i + 6 + k)) after = false;
                            }
                            if (before || after) score += 40;
                        }
                    }
                }
                score += 3 + (run >= 5 ? run - 5 : 0);
            }
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
            {
                int p = y * size + x;
                if (dark[p] && dark[p + 1] && dark[p + size] && dark[p + size + 1]) score += 3;
            }
        int percent = dark.Count(x => x) * 100 / (size * size);
        score += Math.Abs(percent - 50) / 5 * 10;
        return score;
    }

    // Modul an der Stelle (i in Zeile oder Spalte "line"); außerhalb der Matrix gilt es als hell
    private static bool At(bool[] dark, int size, int dir, int line, int i)
    {
        if (i < 0 || i >= size) return false;
        return dark[dir == 0 ? line * size + i : i * size + line];
    }

    // ── Reed-Solomon über GF(256), Primitivpolynom 0x11D ──

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static byte Mul(int a, int b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a & 0xFF] + Log[b & 0xFF]];

    // Rest der Division von Daten · x^n durch das Generatorpolynom (x - a^0)…(x - a^(n-1)).
    // Index ecCount ist der konstante Anteil, Index 0 der höchste.
    private static byte[] ErrorCorrection(byte[] data, int ecCount)
    {
        var gen = new byte[ecCount + 1];
        gen[ecCount] = 1;                                   // Polynom = 1
        for (int i = 0; i < ecCount; i++)                    // mal (x + a^i): neu[j] = alt[j+1] + a^i * alt[j]
            for (int j = 0; j <= ecCount; j++)               // aufsteigend, damit alt[j+1] noch unverändert ist
            {
                int previous = gen[j];
                gen[j] = (byte)((j < ecCount ? gen[j + 1] : 0) ^ Mul(previous, Exp[i]));
            }
        var rem = new byte[ecCount];
        foreach (var b in data)
        {
            int factor = b ^ rem[0];
            for (int j = 0; j < ecCount - 1; j++) rem[j] = rem[j + 1];
            rem[ecCount - 1] = 0;
            if (factor == 0) continue;
            for (int j = 0; j < ecCount; j++) rem[j] ^= Mul(gen[j + 1], factor);
        }
        return rem;
    }

    // 5 Datenbit + 10 Prüfbit: Polynomdivision durch 0x537, dann XOR mit 0x5412 (Maskenmuster der Norm)
    private static int FormatInfo(Ecc level, int mask)
    {
        int ecBits = level switch { Ecc.L => 1, Ecc.M => 0, Ecc.Q => 3, _ => 2 };
        int data = (ecBits << 3) | mask;
        int rem = data << 10;
        for (int i = 14; i >= 10; i--)
            if ((rem >> i & 1) != 0) rem ^= 0x537 << (i - 10);
        return ((data << 10) | (rem & 0x3FF)) ^ 0x5412;
    }

    // 6 Versionsbit + 12 Prüfbit: Polynomdivision durch 0x1F25
    private static int VersionInfo(int version)
    {
        int rem = version << 12;
        for (int i = 17; i >= 12; i--)
            if ((rem >> i & 1) != 0) rem ^= 0x1F25 << (i - 12);
        return (version << 12) | (rem & 0xFFF);
    }

    // ── Ausgabe ──

    // SVG für das Web: ein Rechteck je dunklem Modul, keine Bilder nötig
    public string ToSvg(int module = 4, int margin = 4, string dark = "#000000", string light = "#ffffff")
    {
        int side = (Size + margin * 2) * module;
        var sb = new System.Text.StringBuilder(side * 3);
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{side}\" height=\"{side}\" viewBox=\"0 0 {Size + margin * 2} {Size + margin * 2}\" shape-rendering=\"crispEdges\">");
        sb.Append($"<rect width=\"100%\" height=\"100%\" fill=\"{light}\"/>");
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (_dark[y * Size + x]) sb.Append($"<rect x=\"{x + margin}\" y=\"{y + margin}\" width=\"1\" height=\"1\" fill=\"{dark}\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    // Für die TUI: zwei Zeichen je Modul, mit einem hellen Rand
    public string ToAscii(string on = "██", string off = "  ")
    {
        var sb = new System.Text.StringBuilder();
        for (int y = -1; y <= Size; y++)
        {
            for (int x = -1; x <= Size; x++)
                sb.Append(y >= 0 && y < Size && x >= 0 && x < Size && _dark[y * Size + x] ? on : off);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
