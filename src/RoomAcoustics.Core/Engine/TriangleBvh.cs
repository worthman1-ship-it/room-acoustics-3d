namespace RoomAcoustics.Core;

/// <summary>
/// A bounding-volume hierarchy over a room's triangles, so that a ray or a segment is tested against the few
/// triangles near its path rather than every triangle in the room. Built once, then read-only, so many threads can
/// query it at once. Ties between equally distant hits go to the lower triangle index, so a query's answer does not
/// depend on the order the tree is walked.
/// </summary>
internal sealed class TriangleBvh
{
    private const int LeafSize = 4;
    private const int LinearScanLimit = 32;
    private const double MinHitDistanceM = 1e-9;
    private const double BarycentricTolerance = 1e-12;

    private readonly FaceTriangle[] _triangles;
    private readonly int[] _order;
    private readonly Vec3[] _centroids;
    private readonly List<Node> _nodes = new();
    private Node[] _nodeArray = Array.Empty<Node>();

    private struct Node
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public int Left, Right;   // children, for an inner node
        public int Start, Count;  // a range of _order, for a leaf (Count > 0)
    }

    public TriangleBvh(IReadOnlyList<FaceTriangle> triangles)
    {
        _triangles = triangles.ToArray();
        _order = Enumerable.Range(0, _triangles.Length).ToArray();
        _centroids = _triangles.Select(t => Centroid(t)).ToArray();
        if (_triangles.Length > 0) Build(0, _triangles.Length);
        _nodeArray = _nodes.ToArray();
    }

    private int Build(int start, int end)
    {
        var node = new Node { Start = start, Count = end - start };
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        double cMinX = double.MaxValue, cMinY = double.MaxValue, cMinZ = double.MaxValue;
        double cMaxX = double.MinValue, cMaxY = double.MinValue, cMaxZ = double.MinValue;
        for (var i = start; i < end; i++)
        {
            var t = _triangles[_order[i]];
            minX = Math.Min(minX, Math.Min(t.A.X, Math.Min(t.B.X, t.C.X))); maxX = Math.Max(maxX, Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)));
            minY = Math.Min(minY, Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))); maxY = Math.Max(maxY, Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
            minZ = Math.Min(minZ, Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z))); maxZ = Math.Max(maxZ, Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z)));
            var c = _centroids[_order[i]];
            cMinX = Math.Min(cMinX, c.X); cMaxX = Math.Max(cMaxX, c.X);
            cMinY = Math.Min(cMinY, c.Y); cMaxY = Math.Max(cMaxY, c.Y);
            cMinZ = Math.Min(cMinZ, c.Z); cMaxZ = Math.Max(cMaxZ, c.Z);
        }
        node.MinX = minX; node.MinY = minY; node.MinZ = minZ;
        node.MaxX = maxX; node.MaxY = maxY; node.MaxZ = maxZ;

        var index = _nodes.Count;
        _nodes.Add(node);
        if (end - start <= LeafSize) return index;

        // Split at the median centroid along the widest axis of the centroids.
        var spreadX = cMaxX - cMinX;
        var spreadY = cMaxY - cMinY;
        var spreadZ = cMaxZ - cMinZ;
        var axis = spreadX >= spreadY && spreadX >= spreadZ ? 0 : spreadY >= spreadZ ? 1 : 2;
        // sorted by the centroid's coordinate, equal ones by triangle index, so the tree is the same every time; the keys are computed once rather than in the comparison
        var keys = new (double Key, int Index)[end - start];
        for (var i = start; i < end; i++) keys[i - start] = (Component(_centroids[_order[i]], axis), _order[i]);
        Array.Sort(keys);
        for (var i = start; i < end; i++) _order[i] = keys[i - start].Index;
        var middle = start + (end - start) / 2;
        var left = Build(start, middle);
        var right = Build(middle, end);

        var inner = _nodes[index];
        inner.Count = 0;
        inner.Left = left;
        inner.Right = right;
        _nodes[index] = inner;
        return index;
    }

    private static Vec3 Centroid(FaceTriangle t) => (t.A + t.B + t.C) * (1.0 / 3);

    private static double Component(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>The nearest triangle hit by a ray, ignoring triangles of <paramref name="skipFace"/>; face -1 if none.</summary>
    public (double Length, int Face) Nearest(Vec3 origin, Vec3 direction, int skipFace)
    {
        var best = double.PositiveInfinity;
        var bestTriangle = -1;
        if (_nodeArray.Length == 0) return (best, -1);

        // For a handful of triangles, testing them all is faster than walking the tree.
        if (_triangles.Length <= LinearScanLimit)
        {
            for (var index = 0; index < _triangles.Length; index++)
            {
                ref readonly var triangle = ref _triangles[index];
                if (triangle.Face == skipFace) continue;
                if (TryHit(triangle, origin, direction, out var distance) && distance < best)
                {
                    best = distance;
                    bestTriangle = index;
                }
            }
            return bestTriangle < 0 ? (double.PositiveInfinity, -1) : (best, _triangles[bestTriangle].Face);
        }

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var node = ref _nodeArray[stack[--top]];
            if (!HitsBox(node, origin, direction, best)) continue;

            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var index = _order[i];
                    ref readonly var triangle = ref _triangles[index];
                    if (triangle.Face == skipFace) continue;
                    if (!TryHit(triangle, origin, direction, out var distance)) continue;
                    if (distance < best || (distance == best && index < bestTriangle))
                    {
                        best = distance;
                        bestTriangle = index;
                    }
                }
            }
            else
            {
                stack[top++] = node.Right;
                stack[top++] = node.Left;
            }
        }
        return bestTriangle < 0 ? (double.PositiveInfinity, -1) : (best, _triangles[bestTriangle].Face);
    }

    /// <summary>True if any triangle crosses the segment strictly between <paramref name="margin"/> from each end.</summary>
    public bool IsBlocked(Vec3 from, Vec3 to, double margin)
    {
        var span = to - from;
        var length = span.Length;
        if (length <= 2 * margin || _nodeArray.Length == 0) return false;
        var direction = span * (1 / length);
        var limit = length - margin;

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var node = ref _nodeArray[stack[--top]];
            if (!HitsBox(node, from, direction, limit)) continue;

            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                    if (TryHit(_triangles[_order[i]], from, direction, out var distance) && distance > margin && distance < limit)
                        return true;
            }
            else
            {
                stack[top++] = node.Right;
                stack[top++] = node.Left;
            }
        }
        return false;
    }

    /// <summary>The number of triangle crossings along a ray: odd when the origin is inside a closed surface.</summary>
    public int CountCrossings(Vec3 origin, Vec3 direction)
    {
        var count = 0;
        if (_nodeArray.Length == 0) return 0;

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var node = ref _nodeArray[stack[--top]];
            if (!HitsBox(node, origin, direction, double.PositiveInfinity)) continue;

            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                    if (TryHit(_triangles[_order[i]], origin, direction, out _))
                        count++;
            }
            else
            {
                stack[top++] = node.Right;
                stack[top++] = node.Left;
            }
        }
        return count;
    }

    /// <summary>
    /// The smallest of <paramref name="distance"/> over all the triangles, found without visiting those whose bounding box is farther away than the best so far.
    /// The answer is the same number a loop over every triangle would give.
    /// </summary>
    public double NearestDistance(Vec3 point, Func<Vec3, FaceTriangle, double> distance)
    {
        var best = double.PositiveInfinity;
        if (_nodeArray.Length == 0) return best;

        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var node = ref _nodeArray[stack[--top]];
            // pruned only when the box is clearly farther than the best, so rounding in the box distance can never hide a nearer triangle
            if (BoxDistance(node, point) > best * (1 + 1e-9) + 1e-12) continue;

            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                    best = Math.Min(best, distance(point, _triangles[_order[i]]));
            }
            else
            {
                // the nearer child last, so it is searched first and sets a tight bound for the other
                var left = BoxDistance(_nodeArray[node.Left], point);
                var right = BoxDistance(_nodeArray[node.Right], point);
                if (left <= right) { stack[top++] = node.Right; stack[top++] = node.Left; }
                else { stack[top++] = node.Left; stack[top++] = node.Right; }
            }
        }
        return best;
    }

    private static double BoxDistance(in Node box, Vec3 p)
    {
        var dx = Math.Max(Math.Max(box.MinX - p.X, p.X - box.MaxX), 0);
        var dy = Math.Max(Math.Max(box.MinY - p.Y, p.Y - box.MaxY), 0);
        var dz = Math.Max(Math.Max(box.MinZ - p.Z, p.Z - box.MaxZ), 0);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool HitsBox(in Node box, Vec3 origin, Vec3 direction, double limit)
    {
        var tMin = 0.0;
        var tMax = limit;
        return Slab(origin.X, direction.X, box.MinX, box.MaxX, ref tMin, ref tMax)
            && Slab(origin.Y, direction.Y, box.MinY, box.MaxY, ref tMin, ref tMax)
            && Slab(origin.Z, direction.Z, box.MinZ, box.MaxZ, ref tMin, ref tMax);
    }

    private static bool Slab(double origin, double direction, double min, double max, ref double tMin, ref double tMax)
    {
        // A little slack so triangles lying in a box face, or touching its edge, are not culled by rounding.
        const double slack = 1e-9;
        min -= slack;
        max += slack;
        if (direction == 0) return origin >= min && origin <= max;
        var t1 = (min - origin) / direction;
        var t2 = (max - origin) / direction;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
        return tMin <= tMax;
    }

    /// <summary>Möller-Trumbore ray/triangle intersection.</summary>
    internal static bool TryHit(in FaceTriangle t, Vec3 origin, Vec3 direction, out double distance)
    {
        distance = 0;
        var e1 = t.B - t.A;
        var e2 = t.C - t.A;
        var p = direction.Cross(e2);
        var det = e1.Dot(p);
        if (Math.Abs(det) < 1e-15) return false;
        var inv = 1 / det;
        var s = origin - t.A;
        var u = s.Dot(p) * inv;
        if (u < -BarycentricTolerance || u > 1 + BarycentricTolerance) return false;
        var q = s.Cross(e1);
        var v = direction.Dot(q) * inv;
        if (v < -BarycentricTolerance || u + v > 1 + BarycentricTolerance) return false;
        distance = e2.Dot(q) * inv;
        return distance > MinHitDistanceM;
    }
}
