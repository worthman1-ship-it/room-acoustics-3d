namespace RoomAcoustics.Core;

/// <summary>Faces that close part of a hole: their outline (without any loose sheets inside it), the triangles, the plane's unit normal and the area.</summary>
public sealed record HoleCap(IReadOnlyList<Vec3> Outline, IReadOnlyList<Vec3[]> Triangles, Vec3 Normal, double AreaM2)
{
    public Vec3 Centre => Outline.Aggregate(Vec3.Zero, (sum, p) => sum + p) * (1.0 / Outline.Count);
}

/// <summary>
/// Closes the holes a model's geometry check finds (<see cref="MeshCheck"/>), such as a floor or a ceiling left out of a SketchUp model, with faces
/// across them.
/// </summary>
/// <remarks>
/// <para>
/// A hole is a loop of open edges (each used by one face). If the loop lies in a plane it is closed by one surface. If it does not, but most of it
/// lies in one plane and it leaves that plane only for stretches that each lie in a plane of their own, it is closed in parts: a ceiling missing over
/// a room with a doorway open to the top runs round the walls' tops and dips down the doorway, and is closed as a ceiling (across the doorway's top)
/// and the doorway (which can then be given the Open finish); a ceiling missing with one wall under it, as the ceiling and the wall.
/// </para>
/// <para>
/// Loose sheets lying in the closing surface's plane inside it (ceiling tiles drawn as faces of their own) are left as holes in it, so that the tiles
/// become part of the ceiling. A loop that is itself the outline of such a sheet, with the sheet inside it, is no hole: closing it would draw the
/// sheet twice. Nor is a loop that cannot be followed (a corner where more than two open edges meet), one that encloses no area (a T-junction's
/// sliver or a gap between corners that should have met), or a stretch that twists out of every plane: those are better put right in the model.
/// </para>
/// </remarks>
public sealed class HoleFill
{
    /// <summary>How far a hole's corners may lie off its plane: 2 cm, or 1 % of the hole's size if that is more.</summary>
    public const double FlatnessM = 0.02;

    /// <summary>The smallest surface closed, m²: anything smaller is a sliver or a gap between corners that should have met.</summary>
    public const double MinimumAreaM2 = 0.01;

    private readonly SurfaceMesh _mesh;
    private readonly List<MeshTriangle> _faces;
    private readonly Dictionary<Vec3, int> _index = new();
    private readonly Dictionary<(int, int), int> _openFace = new(); // each open edge (lower vertex first) and the face that has it
    private readonly List<List<int>> _loops = new();                // every loop of open edges that can be followed
    private readonly Dictionary<(int, int), int> _loopOf = new();

    /// <param name="mesh">The model.</param>
    /// <param name="isScreen">The groups that are free-standing screens, which are no part of the room's surface and are left out, as the geometry check leaves them out.</param>
    public HoleFill(SurfaceMesh mesh, Func<string, bool>? isScreen = null)
    {
        _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        for (var i = 0; i < mesh.Vertices.Count; i++) _index.TryAdd(mesh.Vertices[i], i);
        // the faces as the room is built from them, so the open edges are the geometry check's
        _faces = mesh.Faces().Where(t => isScreen?.Invoke(t.Group) != true).ToList();
        var uses = new Dictionary<(int, int), (int Count, int Face)>();
        for (var f = 0; f < _faces.Count; f++)
            foreach (var key in Edges(_faces[f]))
                uses[key] = uses.TryGetValue(key, out var u) ? (u.Count + 1, u.Face) : (1, f);
        foreach (var (key, use) in uses)
            if (use.Count == 1) _openFace[key] = use.Face;

        // the loops: from each corner the open edges go on to exactly one other, or the loop cannot be followed
        var next = new Dictionary<int, List<int>>();
        foreach (var (a, b) in _openFace.Keys)
        {
            (next.TryGetValue(a, out var fromA) ? fromA : next[a] = new List<int>()).Add(b);
            (next.TryGetValue(b, out var fromB) ? fromB : next[b] = new List<int>()).Add(a);
        }
        var done = new HashSet<(int, int)>();
        foreach (var (a0, b0) in _openFace.Keys)
        {
            if (done.Contains((a0, b0))) continue;
            var loop = new List<int> { a0 };
            var (previous, here) = (a0, b0);
            var followed = next[a0].Count == 2;
            while (followed && here != a0)
            {
                if (next[here].Count != 2 || loop.Count > _openFace.Count) { followed = false; break; }
                loop.Add(here);
                var onward = next[here][0] == previous ? next[here][1] : next[here][0];
                (previous, here) = (here, onward);
            }
            for (var k = 0; k < loop.Count; k++) done.Add(Key(loop[k], loop[(k + 1) % loop.Count]));
            done.Add((a0, b0));
            if (!followed) continue;
            for (var k = 0; k < loop.Count; k++) _loopOf[Key(loop[k], loop[(k + 1) % loop.Count])] = _loops.Count;
            _loops.Add(loop);
        }
    }

    /// <summary>The surfaces that close the hole these open edges belong to, or null when it cannot be closed (see the remarks).</summary>
    public IReadOnlyList<HoleCap>? Plan(IReadOnlyList<(Vec3 A, Vec3 B)> hole)
    {
        ArgumentNullException.ThrowIfNull(hole);
        if (hole.Count == 0 || !_index.TryGetValue(hole[0].A, out var a) || !_index.TryGetValue(hole[0].B, out var b)) return null;
        if (!_loopOf.TryGetValue(Key(a, b), out var which)) return null;
        var loop = _loops[which];
        if (IsSheetOutline(loop)) return null;

        var caps = new List<HoleCap>();
        return Close(loop, which, caps, depth: 0) ? caps : null;
    }

    /// <summary>The surfaces that close every hole in the model that can be closed (however many the geometry check lists).</summary>
    public IReadOnlyList<HoleCap> PlanAll()
    {
        var all = new List<HoleCap>();
        for (var which = 0; which < _loops.Count; which++)
        {
            if (IsSheetOutline(_loops[which])) continue;
            var caps = new List<HoleCap>();
            if (Close(_loops[which], which, caps, depth: 0)) all.AddRange(caps);
        }
        return all;
    }

    /// <summary>
    /// Each cap with the name of a group of its own, for where it lies: Filled floor or Filled ceiling for a level one in the bottom or top tenth of the
    /// model's height, Filled surface for any other level one, Filled wall for the rest, numbered when the name is taken.
    /// </summary>
    public static List<(HoleCap Cap, string Group)> Named(SurfaceMesh mesh, IEnumerable<HoleCap> caps)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(caps);
        var (bottom, top) = (mesh.Vertices.Min(v => v.Z), mesh.Vertices.Max(v => v.Z));
        var height = top - bottom;
        var groups = mesh.Groups.ToHashSet();
        var named = new List<(HoleCap, string)>();
        foreach (var cap in caps)
        {
            var name = Math.Abs(cap.Normal.Z) <= 0.9 ? "Filled wall"
                : cap.Centre.Z <= bottom + 0.1 * height ? "Filled floor" : cap.Centre.Z >= top - 0.1 * height ? "Filled ceiling" : "Filled surface";
            var unique = name;
            for (var n = 2; groups.Contains(unique); n++) unique = $"{name} {n}";
            groups.Add(unique);
            named.Add((cap, unique));
        }
        return named;
    }

    /// <summary>The mesh with the caps' triangles added, each cap in the group named with it.</summary>
    public static SurfaceMesh Fill(SurfaceMesh mesh, IEnumerable<(HoleCap Cap, string Group)> caps)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(caps);
        var builder = new SurfaceMeshBuilder();
        foreach (var t in mesh.Triangles) builder.AddTriangle(mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C], t.Group);
        foreach (var (cap, group) in caps)
            foreach (var t in cap.Triangles) builder.AddTriangle(t[0], t[1], t[2], group);
        return builder.Build();
    }

    /// <summary>Closes a loop of corners: whole if it is flat, otherwise as its main plane and the stretches that leave it, each flat.</summary>
    private bool Close(List<int> loop, int ownLoop, List<HoleCap> caps, int depth)
    {
        if (loop.Count < 3 || depth > 8) return false;
        var points = loop.Select(i => _mesh.Vertices[i]).ToList();
        var (normal, area) = Newell(points);
        if (area < MinimumAreaM2) return false;
        var tolerance = Math.Max(FlatnessM, 0.01 * Math.Sqrt(area));
        var unit = normal.Normalized();

        // the plane holding the most corners, among the planes of each corner's two edges (not across the loop's own normal: a ceiling missing with a
        // wall under one side of it leans that between the two, and a plane that leans so can pass through as many corners as either)
        var candidates = new List<Vec3>();
        for (var i = 0; i < points.Count; i++)
        {
            var (before, after) = (points[i] - points[(i + points.Count - 1) % points.Count], points[(i + 1) % points.Count] - points[i]);
            // an edge shorter than the tolerance (two corners that should have been one) has no direction to go by
            if (before.Length < tolerance || after.Length < tolerance) continue;
            var turn = before.Cross(after);
            if (turn.Length < 1e-9 * before.Length * after.Length) continue;
            turn = turn.Normalized();
            if (candidates.All(c => Math.Abs(c.Dot(turn)) < 0.9999)) candidates.Add(turn);
        }
        if (candidates.Count == 0) return false;
        var on = new List<bool>();
        var most = 0;
        foreach (var candidate in candidates)
        {
            var heights = points.Select(p => candidate.Dot(p)).ToList();
            var sorted = heights.OrderBy(h => h).ToList();
            var (best, count) = (sorted[0], 0);
            for (int lo = 0, hi = 0; lo < sorted.Count; lo++)
            {
                while (hi < sorted.Count && sorted[hi] - sorted[lo] <= tolerance) hi++;
                if (hi - lo > count) (best, count) = (sorted[lo], hi - lo);
            }
            if (count <= most) continue;
            most = count;
            on = heights.Select(h => h >= best - 1e-12 && h <= best + tolerance).ToList();
        }
        if (on.All(x => x)) return CapFlat(loop, ownLoop, unit, caps);
        if (on.Count(x => x) < 3) return false;

        // each stretch off the plane runs from a corner on it to the next corner on it; it is cut off by the chord between them and closed by itself
        var start = on.IndexOf(true);
        var main = new List<int>();
        for (var k = 0; k < loop.Count; k++)
        {
            var i = (start + k) % loop.Count;
            if (!on[i]) continue;
            main.Add(loop[i]);
            var j = (i + 1) % loop.Count;
            if (on[j]) continue;
            var stretch = new List<int> { loop[i] };
            while (!on[j]) { stretch.Add(loop[j]); j = (j + 1) % loop.Count; }
            stretch.Add(loop[j]);
            if (!Close(stretch, -1, caps, depth + 1)) return false;
        }
        return Close(main, ownLoop, caps, depth + 1);
    }

    /// <summary>A flat loop closed by one surface, with the loose sheets in its plane inside it left as holes.</summary>
    private bool CapFlat(List<int> loop, int ownLoop, Vec3 unit, List<HoleCap> caps)
    {
        var outline = loop.Select(i => _mesh.Vertices[i]).ToList();
        var (u, v) = Axes(unit);
        var flat = outline.Select(p => (p.Dot(u), p.Dot(v))).ToList();
        var (normal, area) = Newell(outline);
        var tolerance = Math.Max(FlatnessM, 0.01 * Math.Sqrt(area));
        var height = unit.Dot(outline[0]);

        var holes = new List<List<Vec3>>();
        var holeArea = 0.0;
        for (var other = 0; other < _loops.Count; other++)
        {
            if (other == ownLoop) continue;
            var inner = _loops[other].Select(i => _mesh.Vertices[i]).ToList();
            if (inner.Any(p => Math.Abs(unit.Dot(p) - height) > tolerance)) continue;
            if (!inner.All(p => Inside((p.Dot(u), p.Dot(v)), flat)) || !IsSheetOutline(_loops[other])) continue;
            holes.Add(inner);
            holeArea += Newell(inner).Area;
        }

        var triangles = Polygon.Triangulate(outline, holes);
        var covered = triangles.Sum(t => (t[1] - t[0]).Cross(t[2] - t[0]).Length / 2);
        // an outline that crosses itself is not covered exactly: refused
        if (area - holeArea < MinimumAreaM2 || Math.Abs(covered - (area - holeArea)) > 0.01 * area) return false;
        caps.Add(new HoleCap(outline, triangles, normal.Normalized(), area - holeArea));
        return true;
    }

    /// <summary>Whether a loop is the outline of a loose sheet: the faces along it lie in its plane, inside it, so it is drawn and closing it would draw it twice.</summary>
    private bool IsSheetOutline(List<int> loop)
    {
        var points = loop.Select(i => _mesh.Vertices[i]).ToList();
        var (normal, area) = Newell(points);
        if (area < MinimumAreaM2) return false;
        var unit = normal.Normalized();
        var (u, v) = Axes(unit);
        var flat = points.Select(p => (p.Dot(u), p.Dot(v))).ToList();
        var inside = 0;
        for (var k = 0; k < loop.Count; k++)
        {
            var face = _faces[_openFace[Key(loop[k], loop[(k + 1) % loop.Count])]];
            Vec3 a = _mesh.Vertices[face.A], b = _mesh.Vertices[face.B], c = _mesh.Vertices[face.C];
            var faceNormal = (b - a).Cross(c - a).Normalized();
            var centre = (a + b + c) * (1.0 / 3);
            if (Math.Abs(faceNormal.Dot(unit)) > 0.99 && Inside((centre.Dot(u), centre.Dot(v)), flat)) inside++;
        }
        return inside * 2 > loop.Count;
    }

    private static IEnumerable<(int, int)> Edges(MeshTriangle t) => new[] { Key(t.A, t.B), Key(t.B, t.C), Key(t.C, t.A) };

    private static (int, int) Key(int a, int b) => (Math.Min(a, b), Math.Max(a, b));

    /// <summary>Newell's normal of a polygon, whose length is twice its area, and the area.</summary>
    private static (Vec3 Normal, double Area) Newell(IReadOnlyList<Vec3> points)
    {
        var normal = Vec3.Zero;
        for (var i = 0; i < points.Count; i++)
        {
            Vec3 a = points[i], b = points[(i + 1) % points.Count];
            normal += new Vec3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return (normal, normal.Length / 2);
    }

    /// <summary>Two unit axes across a plane of the given unit normal.</summary>
    private static (Vec3 U, Vec3 V) Axes(Vec3 unit)
    {
        var u = Math.Abs(unit.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        u = (u - unit * u.Dot(unit)).Normalized();
        return (u, unit.Cross(u));
    }

    /// <summary>Whether a point lies inside a polygon (even-odd rule).</summary>
    private static bool Inside((double X, double Y) p, IReadOnlyList<(double X, double Y)> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
