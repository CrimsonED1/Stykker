using System.Globalization;
using System.Text;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// STL import (binary and ASCII, coordinates in mm). Vertices are rounded to the 1 nm grid (import rounding,
/// ≤ 0.87 nm, part of the reserve budget) and merged; triangles that become degenerate are dropped.
/// </summary>
public static class StlReader
{
    /// <summary>Reads an STL file.</summary>
    public static Solid Read(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Reads STL data from a stream.</summary>
    public static Solid Read(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.ToArray());
    }

    /// <summary>Reads STL data.</summary>
    public static Solid Read(byte[] data)
    {
        var tris = IsBinary(data) ? ReadBinary(data) : ReadAscii(Encoding.ASCII.GetString(data));
        var index = new Dictionary<Vec3, int>();
        var verts = new List<Vec3>();
        var idx = new List<int>(tris.Count);
        foreach (var p in tris)
        {
            if (!index.TryGetValue(p, out int k)) { k = verts.Count; verts.Add(p); index[p] = k; }
            idx.Add(k);
        }
        return Solid.FromTriangles(verts, idx);
    }

    private static bool IsBinary(byte[] d)
    {
        if (d.Length < 84) return false;
        uint n = BitConverter.ToUInt32(d, 80);
        return 84 + 50L * n == d.Length;
    }

    private static List<Vec3> ReadBinary(byte[] d)
    {
        uint n = BitConverter.ToUInt32(d, 80);
        var pts = new List<Vec3>((int)n * 3);
        for (int t = 0; t < n; t++)
        {
            int o = 84 + 50 * t + 12;
            for (int k = 0; k < 3; k++, o += 12)
                pts.Add(Vec3.Mm(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8)));
        }
        return pts;
    }

    private static List<Vec3> ReadAscii(string text)
    {
        var pts = new List<Vec3>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            pts.Add(Vec3.Mm(
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture)));
        }
        if (pts.Count % 3 != 0) throw new FormatException("ASCII STL: vertex count is not a multiple of 3.");
        return pts;
    }
}
