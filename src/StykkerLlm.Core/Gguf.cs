using System.Collections.Concurrent;
using System.Text;

namespace StykkerLlm.Core;

// Kopfdaten einer GGUF-Datei. Gelesen werden nur Dateikopf, Metadaten und Tensor-Verzeichnis, nie die Gewichte.
public sealed class GgufInfo
{
    public string Path { get; init; } = "";
    public long FileSize { get; init; }
    public int Version { get; init; }
    public long TensorCount { get; init; }
    public long ParameterCount { get; init; }
    public string Architecture { get; init; } = "";
    public string? Name { get; init; }
    public string? SizeLabel { get; init; }
    public int? FileType { get; init; }
    // z. B. "Q4_K_M"; aus general.file_type, sonst der Tensor-Typ mit den meisten Elementen
    public string Quantization { get; init; } = "?";
    // Alle gelesenen Metadaten außer tokenizer.* und langen Texten (Zahlen als long/double, Wahrheitswerte, Strings, kurze Zahlenlisten)
    public IReadOnlyDictionary<string, object> Meta { get; init; } = new Dictionary<string, object>();

    public long? ContextLength => Int($"{Architecture}.context_length");
    public int? BlockCount => (int?)Int($"{Architecture}.block_count");
    public int? EmbeddingLength => (int?)Int($"{Architecture}.embedding_length");
    public int? HeadCount => (int?)Int($"{Architecture}.attention.head_count");
    public int? KeyLength => (int?)Int($"{Architecture}.attention.key_length");
    public int? ValueLength => (int?)Int($"{Architecture}.attention.value_length");
    // Hybridmodelle: nur jede n-te Schicht hat volle Attention (Rest: lineare Attention/SSM ohne wachsenden KV-Cache)
    public int? FullAttentionInterval => (int?)Int($"{Architecture}.full_attention_interval");

    // Anzahl KV-Köpfe je Schicht, nur wenn head_count_kv eine Liste ist (0 = keine Attention); bei einer Zahl null
    public long[]? HeadCountKvPerLayer
    {
        get
        {
            if (!Meta.TryGetValue($"{Architecture}.attention.head_count_kv", out var v)) return null;
            return v as long[];
        }
    }

    public long? Int(string key) => Meta.TryGetValue(key, out var v) ? v switch
    {
        long l => l,
        double d => (long)d,
        long[] { Length: > 0 } a => a[0],
        _ => null,
    } : null;

    public string? Str(string key) => Meta.TryGetValue(key, out var v) ? v as string : null;
}

public sealed class GgufException(string message) : Exception(message);

public static class Gguf
{
    private const int MaxStoredString = 512;
    private const int MaxStoredArray = 1024;

    private static readonly ConcurrentDictionary<string, (long Size, long Mtime, GgufInfo Info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Liest den Kopf (mit Cache nach Pfad, Größe und Änderungszeit). null, wenn die Datei fehlt oder kein lesbares GGUF ist.
    public static GgufInfo? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            long mtime = fi.LastWriteTimeUtc.Ticks;
            if (Cache.TryGetValue(fi.FullName, out var c) && c.Size == fi.Length && c.Mtime == mtime) return c.Info;
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var info = Read(fs, fi.FullName, fi.Length);
            if (Cache.Count > 512) Cache.Clear();
            Cache[fi.FullName] = (fi.Length, mtime, info);
            return info;
        }
        catch { return null; }
    }

    // Liest aus einem Stream (Testbarkeit). Wirft GgufException bei ungültigem Inhalt.
    public static GgufInfo Read(Stream s, string path = "", long fileSize = -1)
    {
        var r = new Reader(s);
        if (r.U32() != 0x46554747) throw new GgufException("not a GGUF file");   // "GGUF"
        uint version = r.U32();
        if (version is 0 or > 0xFFFF) throw new GgufException("unsupported GGUF version or byte order");
        ulong tensorCount = r.U64(), kvCount = r.U64();
        if (tensorCount > 10_000_000 || kvCount > 10_000_000) throw new GgufException("implausible GGUF header");

        var meta = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong i = 0; i < kvCount; i++)
        {
            string key = r.String(4096) ?? throw new GgufException("key too long");
            uint type = r.U32();
            bool keep = !key.StartsWith("tokenizer.", StringComparison.Ordinal);
            var val = ReadValue(r, type, keep);
            if (keep && val != null) meta[key] = val;
        }

        // Tensor-Verzeichnis: Parameterzahl und häufigster Typ (nach Elementen)
        long total = 0;
        var perType = new Dictionary<uint, long>();
        for (ulong i = 0; i < tensorCount; i++)
        {
            r.SkipString();
            uint nd = r.U32();
            if (nd > 8) throw new GgufException("bad tensor rank");
            long elems = 1;
            for (uint d = 0; d < nd; d++) elems = checked(elems * (long)r.U64());
            uint tt = r.U32();
            r.U64();   // Offset
            total += elems;
            perType[tt] = perType.GetValueOrDefault(tt) + elems;
        }

        string arch = meta.TryGetValue("general.architecture", out var a) && a is string sa ? sa : "";
        int? ftype = meta.TryGetValue("general.file_type", out var ft) && ft is long lf ? (int)lf : null;
        string quant = ftype is int f && FileTypeName(f) is { } fn ? fn
            : perType.Count > 0 ? TensorTypeName(perType.MaxBy(k => k.Value).Key) : "?";
        return new GgufInfo
        {
            Path = path, FileSize = fileSize, Version = (int)version, TensorCount = (long)tensorCount, ParameterCount = total,
            Architecture = arch,
            Name = meta.TryGetValue("general.name", out var n) ? n as string : null,
            SizeLabel = meta.TryGetValue("general.size_label", out var sl) ? sl as string : null,
            FileType = ftype, Quantization = quant, Meta = meta,
        };
    }

    // Größe in Byte je Elementtyp (Skalare); 0 = kein Skalar
    private static int ScalarSize(uint t) => t switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };

    private static object? ReadValue(Reader r, uint type, bool keep)
    {
        switch (type)
        {
            case 0: return (long)r.Byte();
            case 1: return (long)(sbyte)r.Byte();
            case 2: return (long)r.U16();
            case 3: return (long)(short)r.U16();
            case 4: return (long)r.U32();
            case 5: return (long)(int)r.U32();
            case 6: return (double)BitConverter.UInt32BitsToSingle(r.U32());
            case 7: return r.Byte() != 0;
            case 8:
                if (!keep) { r.SkipString(); return null; }
                return r.String(MaxStoredString, skipIfLonger: true);
            case 9:
            {
                uint et = r.U32();
                ulong len = r.U64();
                if (len > 1UL << 33) throw new GgufException("array too long");
                int sz = ScalarSize(et);
                if (sz > 0)
                {
                    if (!keep || len > MaxStoredArray || et is 6 or 7 or 12) { r.Skip((long)len * sz); return null; }
                    var arr = new long[len];
                    for (ulong i = 0; i < len; i++) arr[i] = ReadValue(r, et, true) is long l ? l : 0;
                    return arr;
                }
                if (et == 8) { for (ulong i = 0; i < len; i++) r.SkipString(); return null; }   // Strings müssen einzeln übersprungen werden
                if (et == 9) { for (ulong i = 0; i < len; i++) ReadValue(r, 9, false); return null; }
                throw new GgufException("unknown array element type");
            }
            case 10: return (long)r.U64();
            case 11: return (long)r.U64();
            case 12: return BitConverter.UInt64BitsToDouble(r.U64());
            default: throw new GgufException("unknown value type " + type);
        }
    }

    // llama_ftype (general.file_type)
    public static string? FileTypeName(int t) => t switch
    {
        0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 7 => "Q8_0", 8 => "Q5_0", 9 => "Q5_1",
        10 => "Q2_K", 11 => "Q3_K_S", 12 => "Q3_K_M", 13 => "Q3_K_L", 14 => "Q4_K_S", 15 => "Q4_K_M",
        16 => "Q5_K_S", 17 => "Q5_K_M", 18 => "Q6_K", 19 => "IQ2_XXS", 20 => "IQ2_XS", 21 => "Q2_K_S",
        22 => "IQ3_XS", 23 => "IQ3_XXS", 24 => "IQ1_S", 25 => "IQ4_NL", 26 => "IQ3_S", 27 => "IQ3_M",
        28 => "IQ2_S", 29 => "IQ2_M", 30 => "IQ4_XS", 31 => "IQ1_M", 32 => "BF16",
        36 => "TQ1_0", 37 => "TQ2_0", 38 => "MXFP4_MOE",
        40 => "Q1_0", 141 => "PQ2_0",   // Bonsai-Formate (aus echten Dateien und /props model_ftype abgelesen)
        _ => null,
    };

    // ggml_type
    public static string TensorTypeName(uint t) => t switch
    {
        0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 6 => "Q5_0", 7 => "Q5_1", 8 => "Q8_0", 9 => "Q8_1",
        10 => "Q2_K", 11 => "Q3_K", 12 => "Q4_K", 13 => "Q5_K", 14 => "Q6_K", 15 => "Q8_K",
        16 => "IQ2_XXS", 17 => "IQ2_XS", 18 => "IQ3_XXS", 19 => "IQ1_S", 20 => "IQ4_NL", 21 => "IQ3_S", 22 => "IQ2_S",
        23 => "IQ4_XS", 24 => "I8", 25 => "I16", 26 => "I32", 27 => "I64", 28 => "F64", 29 => "IQ1_M", 30 => "BF16",
        34 => "TQ1_0", 35 => "TQ2_0", 39 => "MXFP4", 41 => "Q1_0", 142 => "PQ2_0",
        _ => "type " + t,
    };

    // Kleiner Leser mit Überspringen; bricht bei Dateiende mit GgufException ab
    private sealed class Reader(Stream s)
    {
        private readonly byte[] _b = new byte[8];

        private void Fill(int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(_b, got, n - got);
                if (r <= 0) throw new GgufException("unexpected end of file");
                got += r;
            }
        }

        public byte Byte() { Fill(1); return _b[0]; }
        public ushort U16() { Fill(2); return BitConverter.ToUInt16(_b, 0); }
        public uint U32() { Fill(4); return BitConverter.ToUInt32(_b, 0); }
        public ulong U64() { Fill(8); return BitConverter.ToUInt64(_b, 0); }

        public void Skip(long n)
        {
            if (n < 0) throw new GgufException("negative skip");
            if (n == 0) return;
            if (s.CanSeek)
            {
                long pos = s.Position + n;
                if (pos > s.Length) throw new GgufException("unexpected end of file");
                s.Position = pos;
            }
            else
            {
                var buf = new byte[8192];
                while (n > 0) { int r = s.Read(buf, 0, (int)Math.Min(buf.Length, n)); if (r <= 0) throw new GgufException("unexpected end of file"); n -= r; }
            }
        }

        public void SkipString() => Skip(Len());

        private long Len()
        {
            ulong len = U64();
            if (len > 1UL << 32) throw new GgufException("string too long");
            return (long)len;
        }

        // null: länger als max (bei skipIfLonger wird übersprungen und "" geliefert, sonst Fehler)
        public string? String(int max, bool skipIfLonger = false)
        {
            long len = Len();
            if (len > max)
            {
                if (!skipIfLonger) return null;
                Skip(len);
                return "";
            }
            var buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int r = s.Read(buf, got, (int)len - got);
                if (r <= 0) throw new GgufException("unexpected end of file");
                got += r;
            }
            return Encoding.UTF8.GetString(buf);
        }
    }
}
