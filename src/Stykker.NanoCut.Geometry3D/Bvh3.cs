namespace Stykker.NanoCut.Geometry3D;

/// <summary>Static bounding volume hierarchy over faces (median split on the longest axis).</summary>
internal sealed class Bvh3
{
    private const int LeafSize = 8;

    private readonly IReadOnlyList<Face3> _faces;
    private readonly int[] _order;
    private readonly List<Node> _nodes = [];
    private double[]? _keys;

    private struct Node
    {
        public Box3 Box;
        public int Left, Right; // children, or -1 for leaves
        public int Start, Count;
    }

    public Bvh3(IReadOnlyList<Face3> faces)
    {
        _faces = faces;
        _order = Enumerable.Range(0, faces.Count).ToArray();
        if (faces.Count > 0) Build(0, faces.Count);
    }

    public Box3 Bounds => _nodes.Count > 0 ? _nodes[0].Box : Box3.Empty;

    private int Build(int start, int count)
    {
        var box = Box3.Empty;
        for (int i = start; i < start + count; i++) box = box.Union(_faces[_order[i]].Box);
        int index = _nodes.Count;
        _nodes.Add(new Node { Box = box, Left = -1, Right = -1, Start = start, Count = count });
        if (count <= LeafSize) return index;

        double dx = box.MaxX - box.MinX, dy = box.MaxY - box.MinY, dz = box.MaxZ - box.MinZ;
        int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;
        // Sort the range by box centre with a key array (no comparison delegate).
        var keys = _keys ??= new double[_order.Length];
        for (int i = start; i < start + count; i++) keys[i] = _faces[_order[i]].Box.Center(axis);
        Array.Sort(keys, _order, start, count);
        int half = count / 2;
        int left = Build(start, half);
        int right = Build(start + half, count - half);
        var node = _nodes[index];
        node.Left = left;
        node.Right = right;
        _nodes[index] = node;
        return index;
    }

    /// <summary>Faces whose box overlaps <paramref name="box"/>.</summary>
    public void Query(in Box3 box, List<Face3> result)
    {
        result.Clear();
        if (_nodes.Count == 0) return;
        // Depth is about log2(faces / LeafSize); 256 entries cover any realistic tree without allocating.
        Span<int> stack = stackalloc int[256];
        int top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var n = _nodes[stack[--top]];
            if (!n.Box.Overlaps(box)) continue;
            if (n.Left < 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                {
                    var f = _faces[_order[i]];
                    if (f.Box.Overlaps(box)) result.Add(f);
                }
            }
            else
            {
                if (top + 2 > stack.Length) throw new InvalidOperationException("BVH deeper than expected.");
                stack[top++] = n.Left;
                stack[top++] = n.Right;
            }
        }
    }

    /// <summary>Faces whose box meets the ray {(x, y, z) : x ≥ x0}.</summary>
    public void QueryRayX(double x0, double y, double z, List<Face3> result) =>
        Query(new Box3(x0, y, z, double.MaxValue, y, z), result);
}
