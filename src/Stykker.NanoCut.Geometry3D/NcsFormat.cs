using System.Buffers.Binary;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// The internal, lossless solid format ".ncs" (NanoCut Solid). It stores the exact plane-based representation:
/// a plane table (four Int128 coefficients), a vertex table (grid points as three Int64 in nm, or exact
/// homogeneous points as four Int384) and faces as indices into both. Loading returns a bit-identical solid,
/// so results can be stored and processed further without any rounding.
/// </summary>
/// <remarks>
/// Layout (little-endian): "NCS1", uint32 version = 1, uint32 planeCount, planes (64 bytes each),
/// uint32 vertexCount, vertices (byte kind: 0 = grid + 24 bytes, 1 = exact + 192 bytes), uint32 faceCount,
/// faces (uint32 support, uint32 n, n × uint32 edge plane, n × uint32 vertex).
/// </remarks>
public static class NcsFormat
{
    private static readonly byte[] Magic = "NCS1"u8.ToArray();
    private const uint Version = 1;

    /// <summary>Writes a solid to a file.</summary>
    public static void Save(Solid solid, string path)
    {
        using var f = File.Create(path);
        Save(solid, f);
    }

    /// <summary>Writes a solid to a stream.</summary>
    public static void Save(Solid solid, Stream stream)
    {
        var planeIndex = new Dictionary<Plane3, int>();
        var planes = new List<Plane3>();
        var gridIndex = new Dictionary<Vec3, int>();
        var exactIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var vertices = new List<Point3>();

        int PlaneId(in Plane3 p)
        {
            if (!planeIndex.TryGetValue(p, out int k)) { k = planes.Count; planes.Add(p); planeIndex[p] = k; }
            return k;
        }
        int VertexId(in Point3 v)
        {
            int k;
            if (v.IsGrid)
            {
                if (!gridIndex.TryGetValue(v.Grid, out k)) { k = vertices.Count; vertices.Add(v); gridIndex[v.Grid] = k; }
            }
            else if (!exactIndex.TryGetValue(v.Identity!, out k)) { k = vertices.Count; vertices.Add(v); exactIndex[v.Identity!] = k; }
            return k;
        }

        var faces = new List<(int Support, int[] Edges, int[] Verts)>(solid.Faces.Count);
        foreach (var f in solid.Faces)
            faces.Add((PlaneId(f.Support), f.Edges.Select(e => PlaneId(e)).ToArray(), f.Vertices.Select(v => VertexId(v)).ToArray()));

        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write((uint)planes.Count);
        Span<byte> buf = stackalloc byte[Int384.ByteCount];
        foreach (var p in planes)
        {
            WriteInt128(w, p.Nx); WriteInt128(w, p.Ny); WriteInt128(w, p.Nz); WriteInt128(w, p.D);
        }
        w.Write((uint)vertices.Count);
        foreach (var v in vertices)
        {
            if (v.IsGrid)
            {
                w.Write((byte)0);
                w.Write(v.Grid.X); w.Write(v.Grid.Y); w.Write(v.Grid.Z);
            }
            else
            {
                w.Write((byte)1);
                var h = v.Homogeneous;
                foreach (var c in new[] { h.X, h.Y, h.Z, h.W })
                {
                    c.WriteBytes(buf);
                    w.Write(buf);
                }
            }
        }
        w.Write((uint)faces.Count);
        foreach (var (support, edges, verts) in faces)
        {
            w.Write((uint)support);
            w.Write((uint)verts.Length);
            foreach (var e in edges) w.Write((uint)e);
            foreach (var v in verts) w.Write((uint)v);
        }
    }

    /// <summary>Reads a solid from a file.</summary>
    public static Solid Load(string path)
    {
        using var f = File.OpenRead(path);
        return Load(f);
    }

    /// <summary>Reads a solid from a stream.</summary>
    public static Solid Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new FormatException("Not an NCS file.");
        uint version = r.ReadUInt32();
        if (version != Version) throw new FormatException($"Unsupported NCS version {version}.");
        var planes = new Plane3[r.ReadUInt32()];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = new Plane3(ReadInt128(r), ReadInt128(r), ReadInt128(r), ReadInt128(r));
        var vertices = new Point3[r.ReadUInt32()];
        for (int i = 0; i < vertices.Length; i++)
        {
            byte kind = r.ReadByte();
            if (kind == 0)
            {
                vertices[i] = new Point3(new Vec3(r.ReadInt64(), r.ReadInt64(), r.ReadInt64()));
            }
            else if (kind == 1)
            {
                Int384 Read() => Int384.ReadBytes(r.ReadBytes(Int384.ByteCount));
                vertices[i] = new Point3(new HomogeneousPoint3(Read(), Read(), Read(), Read()));
            }
            else throw new FormatException($"Unknown vertex kind {kind}.");
        }
        var faces = new Face3[r.ReadUInt32()];
        for (int i = 0; i < faces.Length; i++)
        {
            var support = planes[r.ReadUInt32()];
            int n = (int)r.ReadUInt32();
            var edges = new Plane3[n];
            var verts = new Point3[n];
            for (int k = 0; k < n; k++) edges[k] = planes[r.ReadUInt32()];
            for (int k = 0; k < n; k++) verts[k] = vertices[r.ReadUInt32()];
            faces[i] = new Face3(support, edges, verts);
        }
        return new Solid(faces);
    }

    private static void WriteInt128(BinaryWriter w, Int128 v)
    {
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(b, v);
        w.Write(b);
    }

    private static Int128 ReadInt128(BinaryReader r) => BinaryPrimitives.ReadInt128LittleEndian(r.ReadBytes(16));
}
