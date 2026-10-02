namespace Stykker.NanoCut.Geometry3D;

/// <summary>Static bounding volume hierarchy over faces (median split on the longest axis).</summary>
internal sealed class Bvh3
{
    private const int LeafSize = 8;

    private readonly IReadOnlyList<Face3> _faces;
    private readonly int[] _order;
    private double[]? _keys;
    // Nodes in a flat array: leaves hold more than LeafSize / 2 faces, so there are at most n/2 + 1 nodes (+ slack).
    private readonly Node[] _nodes;
    private int _nodeCount;
    // Query-time copies in tree order: face boxes and faces as arrays (no list indexers, no property copies).
    private readonly Box3[] _boxes;
    private readonly Face3[] _sorted;

    private struct Node
    {
        public Box3 Box;
        public int Left, Right; // children, or -1 for leaves
        public int Start, Count;
    }

    public Bvh3(IReadOnlyList<Face3> faces)
    {
        _faces = faces;
        _order = new int[faces.Count];
        for (int i = 0; i < _order.Length; i++) _order[i] = i;
        _nodes = new Node[faces.Count / 2 + 4];
        if (faces.Count > 0) Build(0, faces.Count);
        _sorted = new Face3[faces.Count];
        _boxes = new Box3[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            _sorted[i] = faces[_order[i]];
            _boxes[i] = _sorted[i].Box;
        }
    }

    public Box3 Bounds => _nodeCount > 0 ? _nodes[0].Box : Box3.Empty;

    private int Build(int start, int count)
    {
        var box = Box3.Empty;
        for (int i = start; i < start + count; i++) box = box.Union(_faces[_order[i]].Box);
        int index = _nodeCount++;
        _nodes[index] = new Node { Box = box, Left = -1, Right = -1, Start = start, Count = count };
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
        _nodes[index].Left = left;
        _nodes[index].Right = right;
        return index;
    }

    /// <summary>Faces whose box overlaps <paramref name="box"/>.</summary>
    public void Query(in Box3 box, List<Face3> result)
    {
        result.Clear();
        if (_nodeCount == 0) return;
        // Depth is about log2(faces / LeafSize); 256 entries cover any realistic tree without allocating.
        Span<int> stack = stackalloc int[256];
        int top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var n = ref _nodes[stack[--top]];
            if (!n.Box.Overlaps(box)) continue;
            if (n.Left < 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                    if (_boxes[i].Overlaps(box)) result.Add(_sorted[i]);
            }
            else
            {
                if (top + 2 > stack.Length) throw new InvalidOperationException("BVH deeper than expected.");
                stack[top++] = n.Left;
                stack[top++] = n.Right;
            }
        }
    }

    /// <summary>Faces whose box meets the axis-parallel ray from (x, y, z) along ±axis (0 = x, 1 = y, 2 = z).</summary>
    public void QueryRay(int axis, int sign, double x, double y, double z, List<Face3> result)
    {
        double lo(double c, int a) => a != axis ? c : sign > 0 ? c - 1 : double.MinValue;
        double hi(double c, int a) => a != axis ? c : sign > 0 ? double.MaxValue : c + 1;
        Query(new Box3(lo(x, 0), lo(y, 1), lo(z, 2), hi(x, 0), hi(y, 1), hi(z, 2)), result);
    }

    /// <summary>Faces whose box meets the ray {(x, y, z) : x ≥ x0}.</summary>
    public void QueryRayX(double x0, double y, double z, List<Face3> result) =>
        Query(new Box3(x0, y, z, double.MaxValue, y, z), result);
}
