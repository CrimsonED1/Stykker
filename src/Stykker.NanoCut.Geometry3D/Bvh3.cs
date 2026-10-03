using System.Buffers;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// Static bounding volume hierarchy over faces (median split on the longest axis). Its arrays are rented from the shared
/// pools (they exceed the large-object threshold for big solids); dispose it to return them.
/// </summary>
internal sealed class Bvh3 : IDisposable
{
    private const int LeafSize = 8;

    private readonly IReadOnlyList<Face3> _faces;
    private readonly int _count;
    private int[] _order;
    private double[]? _keys;
    // Nodes in a flat array: splitting more than LeafSize faces gives children of at least LeafSize / 2, so there are at
    // most n / 4 leaves and fewer than n / 2 nodes (+ slack for tiny inputs).
    private Node[] _nodes;
    private int _nodeCount;
    private bool _disposed;
    // Query-time copies in tree order: face boxes and faces as arrays (no list indexers, no property copies).
    private Box3[] _boxes;
    private Face3[] _sorted;

    private struct Node
    {
        public Box3 Box;
        public int Left, Right; // children, or -1 for leaves
        public int Start, Count;
    }

    public Bvh3(IReadOnlyList<Face3> faces)
    {
        _faces = faces;
        _count = faces.Count;
        _order = ArrayPool<int>.Shared.Rent(_count);
        for (int i = 0; i < _count; i++) _order[i] = i;
        _nodes = ArrayPool<Node>.Shared.Rent(_count / 2 + 4);
        if (_count > 0) Build(0, _count);
        _sorted = ArrayPool<Face3>.Shared.Rent(_count);
        _boxes = ArrayPool<Box3>.Shared.Rent(_count);
        for (int i = 0; i < _count; i++)
        {
            _sorted[i] = faces[_order[i]];
            _boxes[i] = _sorted[i].Box;
        }
        if (_keys is not null) { ArrayPool<double>.Shared.Return(_keys); _keys = null; }
    }

    /// <summary>Returns the pooled arrays. The hierarchy must not be used afterwards.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<int>.Shared.Return(_order);
        ArrayPool<Node>.Shared.Return(_nodes);
        ArrayPool<Face3>.Shared.Return(_sorted, clearArray: true); // drop the face references
        ArrayPool<Box3>.Shared.Return(_boxes);
        _order = []; _nodes = []; _sorted = []; _boxes = [];
        _nodeCount = 0;
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
        var keys = _keys ??= ArrayPool<double>.Shared.Rent(_count);
        for (int i = start; i < start + count; i++) keys[i] = _faces[_order[i]].Box.Center(axis);
        int half = count / 2;
        // Only the median split matters: partition around the half-th key instead of sorting the whole range.
        Select(keys, _order, start, start + count - 1, start + half);
        int left = Build(start, half);
        int right = Build(start + half, count - half);
        _nodes[index].Left = left;
        _nodes[index].Right = right;
        return index;
    }

    // Quickselect (Hoare partition, median-of-three pivot, sorting fallback): afterwards keys[k] is the k-th smallest of keys[lo..hi], with
    // smaller-or-equal keys before it and greater-or-equal keys after it; order[] is permuted alongside.
    private static void Select(double[] keys, int[] order, int lo, int hi, int k)
    {
        // Introselect: after 2·log2(n) + 4 rounds without converging (adversarial key order), sort the rest instead,
        // which bounds the work by O(n log n).
        int budget = 2 * System.Numerics.BitOperations.Log2((uint)(hi - lo + 1)) + 4;
        while (hi > lo)
        {
            if (budget-- == 0)
            {
                Array.Sort(keys, order, lo, hi - lo + 1);
                return;
            }
            int mid = lo + (hi - lo) / 2;
            if (keys[mid] < keys[lo]) Swap(lo, mid);
            if (keys[hi] < keys[lo]) Swap(lo, hi);
            if (keys[hi] < keys[mid]) Swap(mid, hi);
            double pivot = keys[mid];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (keys[i] < pivot) i++;
                while (keys[j] > pivot) j--;
                if (i <= j) { Swap(i, j); i++; j--; }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }

        void Swap(int a, int b)
        {
            (keys[a], keys[b]) = (keys[b], keys[a]);
            (order[a], order[b]) = (order[b], order[a]);
        }
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
