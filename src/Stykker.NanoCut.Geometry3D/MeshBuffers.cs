using System.Text;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>Where the float32 buffer coordinates are measured from.</summary>
public enum OriginMode
{
    /// <summary>Absolute millimetres (float32 loses accuracy far from the origin).</summary>
    Absolute,

    /// <summary>Relative to the centre of the bounding box, so float32 stays accurate for display.</summary>
    Centroid,
}

/// <summary>
/// Raw triangle buffers: positions and normals as float32 (mm, relative to <see cref="OriginMm"/>), indices as uint32.
/// Maps directly to three.js BufferGeometry and Babylon.js VertexData.
/// </summary>
public sealed class MeshBuffers
{
    private MeshBuffers(float[] positions, float[] normals, uint[] indices, (double X, double Y, double Z) origin)
    {
        Positions = positions;
        Normals = normals;
        Indices = indices;
        OriginMm = origin;
    }

    /// <summary>x, y, z per vertex in mm relative to <see cref="OriginMm"/>.</summary>
    public float[] Positions { get; }

    /// <summary>Unit normal per vertex (flat per face).</summary>
    public float[] Normals { get; }

    /// <summary>Three indices per triangle, counter-clockwise seen from outside.</summary>
    public uint[] Indices { get; }

    /// <summary>Origin of the buffer coordinates in absolute mm.</summary>
    public (double X, double Y, double Z) OriginMm { get; }

    /// <summary>Number of triangles.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// One buffer with the triangles of several buffers (e.g. the cells of a workpiece or many small parts), all
    /// relative to the origin of the first one.
    /// </summary>
    public static MeshBuffers Concat(IEnumerable<MeshBuffers> buffers)
    {
        var list = buffers.ToList();
        if (list.Count == 0) return new MeshBuffers([], [], [], (0, 0, 0));
        var o = list[0].OriginMm;
        var pos = new float[list.Sum(b => b.Positions.Length)];
        var nrm = new float[pos.Length];
        var idx = new uint[list.Sum(b => b.Indices.Length)];
        int p = 0, k = 0;
        foreach (var b in list)
        {
            float dx = (float)(b.OriginMm.X - o.X), dy = (float)(b.OriginMm.Y - o.Y), dz = (float)(b.OriginMm.Z - o.Z);
            uint start = (uint)(p / 3);
            for (int i = 0; i < b.Positions.Length; i += 3)
            {
                pos[p + i] = b.Positions[i] + dx;
                pos[p + i + 1] = b.Positions[i + 1] + dy;
                pos[p + i + 2] = b.Positions[i + 2] + dz;
            }
            Array.Copy(b.Normals, 0, nrm, p, b.Normals.Length);
            foreach (uint i in b.Indices) idx[k++] = start + i;
            p += b.Positions.Length;
        }
        return new MeshBuffers(pos, nrm, idx, o);
    }

    internal static MeshBuffers From(Solid solid, OriginMode mode)
    {
        (double X, double Y, double Z) o = (0, 0, 0);
        if (mode == OriginMode.Centroid && solid.BoundsMm is { } b)
            o = ((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, (b.MinZ + b.MaxZ) / 2);
        var pos = new List<float>();
        var nrm = new List<float>();
        var idx = new List<uint>();
        foreach (var f in solid.Faces)
        {
            var s = f.Support;
            double nx = (double)s.Nx, ny = (double)s.Ny, nz = (double)s.Nz;
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            uint start = (uint)(pos.Count / 3);
            foreach (var v in f.Vertices)
            {
                pos.Add((float)(v.X * 1e-6 - o.X));
                pos.Add((float)(v.Y * 1e-6 - o.Y));
                pos.Add((float)(v.Z * 1e-6 - o.Z));
                nrm.Add((float)(nx / l));
                nrm.Add((float)(ny / l));
                nrm.Add((float)(nz / l));
            }
            for (int i = 1; i + 1 < f.Vertices.Length; i++)
            {
                idx.Add(start);
                idx.Add(start + (uint)i);
                idx.Add(start + (uint)i + 1);
            }
        }
        return new MeshBuffers(pos.ToArray(), nrm.ToArray(), idx.ToArray(), o);
    }

    /// <summary>Binary STL (absolute mm).</summary>
    public byte[] ToStl()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(new byte[80]);
        w.Write((uint)TriangleCount);
        for (int t = 0; t < Indices.Length; t += 3)
        {
            uint a = Indices[t];
            w.Write(Normals[3 * a]); w.Write(Normals[3 * a + 1]); w.Write(Normals[3 * a + 2]);
            for (int k = 0; k < 3; k++)
            {
                uint i = Indices[t + k];
                w.Write((float)(Positions[3 * i] + OriginMm.X));
                w.Write((float)(Positions[3 * i + 1] + OriginMm.Y));
                w.Write((float)(Positions[3 * i + 2] + OriginMm.Z));
            }
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>JSON with the three buffers (for the JS adapters and demos).</summary>
    public string ToJson()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"{{\"origin\":[{OriginMm.X},{OriginMm.Y},{OriginMm.Z}],\"positions\":[");
        sb.AppendJoin(',', Positions.Select(p => p.ToString("R", inv)));
        sb.Append("],\"normals\":[");
        sb.AppendJoin(',', Normals.Select(p => p.ToString("R", inv)));
        sb.Append("],\"indices\":[");
        sb.AppendJoin(',', Indices);
        sb.Append("]}");
        return sb.ToString();
    }
}
