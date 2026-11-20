namespace RoomAcoustics.Core;

/// <summary>
/// Changes to an imported model's shape made in the program: a corner moved, a face drawn between corners, a flat surface deleted. Each gives a new mesh.
/// </summary>
/// <remarks>
/// Corners and faces are found by where they are, to within <see cref="CornerToleranceM"/>, not by number, so a saved project's edits find them again in
/// the model read afresh. Corners that come to lie together are joined, and faces left with no area are dropped.
/// </remarks>
public static class MeshEdit
{
    /// <summary>How near a point must be to a corner to be that corner, m.</summary>
    public const double CornerToleranceM = 0.001;

    /// <summary>How far a drawn face's corners may lie off its plane: 2 cm, or 1 % of the face's size if that is more (as for a filled hole).</summary>
    public const double FlatnessM = HoleFill.FlatnessM;

    /// <summary>The smallest face drawn, m².</summary>
    public const double MinimumAreaM2 = HoleFill.MinimumAreaM2;

    /// <summary>The mesh with the corner at <paramref name="from"/> moved to <paramref name="to"/>, and every face that meets there with it.</summary>
    public static SurfaceMesh MoveCorner(SurfaceMesh mesh, Vec3 from, Vec3 to)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var moving = Enumerable.Range(0, mesh.Vertices.Count).Where(i => (mesh.Vertices[i] - from).Length <= CornerToleranceM).ToHashSet();
        if (moving.Count == 0) throw new ArgumentException($"The model has no corner at {Text(from)}.");
        var vertices = mesh.Vertices.Select((v, i) => moving.Contains(i) ? to : v).ToList();
        var builder = new SurfaceMeshBuilder();
        foreach (var t in mesh.Triangles) builder.AddTriangle(vertices[t.A], vertices[t.B], vertices[t.C], t.Group);
        return WithoutClosedUp(builder.Build());
    }

    /// <summary>
    /// The mesh with a flat face added in <paramref name="group"/> (a new name makes a new group), its corners in order round it; it may be concave.
    /// A corner within <see cref="CornerToleranceM"/> of one of the model's is that corner, so the face joins the faces round it.
    /// </summary>
    public static SurfaceMesh AddFace(SurfaceMesh mesh, IReadOnlyList<Vec3> corners, string group)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(corners);
        group = group?.Trim() ?? "";
        if (group.Length == 0) throw new ArgumentException("Say which surface group the face goes in.");

        // a corner given twice running (or the first again at the end) counts once
        var outline = new List<Vec3>();
        foreach (var p in corners.Select(p => mesh.Vertices.FirstOrDefault(v => (v - p).Length <= CornerToleranceM, p)))
            if (outline.Count == 0 || (p - outline[^1]).Length > CornerToleranceM) outline.Add(p);
        if (outline.Count > 1 && (outline[0] - outline[^1]).Length <= CornerToleranceM) outline.RemoveAt(outline.Count - 1);
        if (outline.Count < 3) throw new ArgumentException("A face needs at least three corners.");

        // the plane (Newell's normal, whose length is twice the area enclosed), how far off it the corners are, and whether the outline crosses itself
        var normal = Vec3.Zero;
        for (var i = 0; i < outline.Count; i++) normal += outline[i].Cross(outline[(i + 1) % outline.Count]);
        var area = normal.Length / 2;
        if (area < MinimumAreaM2) throw new ArgumentException("The corners enclose no area: go round the face in order.");
        var centre = outline.Aggregate(Vec3.Zero, (sum, p) => sum + p) * (1.0 / outline.Count);
        var unit = normal.Normalized();
        var off = outline.Max(p => Math.Abs(unit.Dot(p - centre)));
        var size = outline.Max(p => (p - centre).Length) * 2;
        if (off > Math.Max(FlatnessM, 0.01 * size)) throw new ArgumentException($"The corners are not in one plane: one is {off * 100:0.#} cm off it.");
        var triangles = Polygon.Triangulate(outline);
        var covered = triangles.Sum(t => (t[1] - t[0]).Cross(t[2] - t[0]).Length / 2);
        if (Math.Abs(covered - area) > 0.01 * area) throw new ArgumentException("The face's edges cross: go round it in order.");

        var builder = new SurfaceMeshBuilder();
        foreach (var t in mesh.Triangles) builder.AddTriangle(mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C], t.Group);
        foreach (var t in triangles) builder.AddTriangle(t[0], t[1], t[2], group);
        return builder.Build();
    }

    /// <summary>
    /// The mesh without the flat surface the face with corners <paramref name="a"/>, <paramref name="b"/> and <paramref name="c"/> is part of: that face and
    /// the faces of its group joined to it edge to edge in its plane (a wall drawn in several pieces goes whole), and the corners only they used.
    /// </summary>
    public static SurfaceMesh RemoveSurface(SurfaceMesh mesh, Vec3 a, Vec3 b, Vec3 c)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        bool At(int vertex, Vec3 p) => (mesh.Vertices[vertex] - p).Length <= CornerToleranceM;
        var given = new[] { a, b, c };
        var start = Enumerable.Range(0, mesh.Triangles.Count).FirstOrDefault(i =>
        {
            var t = mesh.Triangles[i];
            var own = new[] { t.A, t.B, t.C };
            return given.All(p => own.Any(v => At(v, p))) && own.All(v => given.Any(p => At(v, p)));
        }, -1);
        if (start < 0) throw new ArgumentException($"The model has no face with corners {Text(a)}, {Text(b)} and {Text(c)}.");

        var group = mesh.Triangles[start].Group;
        Vec3 Normal(MeshTriangle t) => (mesh.Vertices[t.B] - mesh.Vertices[t.A]).Cross(mesh.Vertices[t.C] - mesh.Vertices[t.A]);
        var unit = Normal(mesh.Triangles[start]).Normalized();
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < mesh.Triangles.Count; i++)
            if (mesh.Triangles[i].Group == group)
                foreach (var e in Edges(mesh.Triangles[i]))
                    (byEdge.TryGetValue(e, out var list) ? list : byEdge[e] = new List<int>()).Add(i);
        var gone = new HashSet<int> { start };
        var queue = new Queue<int>(gone);
        while (queue.Count > 0)
            foreach (var e in Edges(mesh.Triangles[queue.Dequeue()]))
                foreach (var n in byEdge[e])
                {
                    var normal = Normal(mesh.Triangles[n]);
                    if (!gone.Contains(n) && normal.Length > 1e-12 && Math.Abs(unit.Dot(normal) / normal.Length) > 0.9995 && gone.Add(n)) queue.Enqueue(n);
                }
        if (gone.Count == mesh.Triangles.Count) throw new ArgumentException("That is the model's only surface: it cannot be deleted.");
        return Compact(mesh.Vertices, mesh.Triangles.Where((_, i) => !gone.Contains(i)).ToList());
    }

    private static IEnumerable<(int, int)> Edges(MeshTriangle t)
    {
        yield return (Math.Min(t.A, t.B), Math.Max(t.A, t.B));
        yield return (Math.Min(t.B, t.C), Math.Max(t.B, t.C));
        yield return (Math.Min(t.C, t.A), Math.Max(t.C, t.A));
    }

    /// <summary>The mesh without faces whose corners have come together, or that have no area left.</summary>
    private static SurfaceMesh WithoutClosedUp(SurfaceMesh mesh) =>
        Compact(mesh.Vertices, mesh.Triangles.Where(t => t.A != t.B && t.B != t.C && t.C != t.A
            && (mesh.Vertices[t.B] - mesh.Vertices[t.A]).Cross(mesh.Vertices[t.C] - mesh.Vertices[t.A]).Length > 1e-9).ToList());

    /// <summary>The faces with only the corners they use, renumbered in order.</summary>
    private static SurfaceMesh Compact(IReadOnlyList<Vec3> vertices, List<MeshTriangle> triangles)
    {
        var used = triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Order().ToList();
        var index = used.Select((old, i) => (old, i)).ToDictionary(p => p.old, p => p.i);
        return new(used.Select(i => vertices[i]).ToList(), triangles.Select(t => t with { A = index[t.A], B = index[t.B], C = index[t.C] }).ToList());
    }

    private static string Text(Vec3 p) => $"({p.X:0.###}, {p.Y:0.###}, {p.Z:0.###})";
}
