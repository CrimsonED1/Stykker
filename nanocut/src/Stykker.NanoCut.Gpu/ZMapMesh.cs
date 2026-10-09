namespace Stykker.NanoCut.Gpu;

/// <summary>
/// Triangle buffers of a Z-map: positions and normals as float32 (mm, relative to <see cref="OriginMm"/>), indices
/// as uint32. The layout matches what the viewer in <c>js/</c> consumes, so the buffers can be sent as raw bytes.
/// </summary>
public sealed class ZMapMesh
{
    internal ZMapMesh(float[] positions, float[] normals, uint[] indices, (double X, double Y, double Z) origin)
    {
        Positions = positions;
        Normals = normals;
        Indices = indices;
        OriginMm = origin;
    }

    /// <summary>x, y, z per vertex in mm relative to <see cref="OriginMm"/>.</summary>
    public float[] Positions { get; }

    /// <summary>Unit normal per vertex, from the slope of the height field.</summary>
    public float[] Normals { get; }

    /// <summary>Three indices per triangle, counter-clockwise seen from above.</summary>
    public uint[] Indices { get; }

    /// <summary>Origin of the buffer coordinates in absolute mm.</summary>
    public (double X, double Y, double Z) OriginMm { get; }

    /// <summary>Number of triangles.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>Number of vertices.</summary>
    public int VertexCount => Positions.Length / 3;
}
