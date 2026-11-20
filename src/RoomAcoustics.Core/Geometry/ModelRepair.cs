namespace RoomAcoustics.Core;

/// <summary>What a repair did: the model after it, and a line for each step that changed something.</summary>
public sealed record RepairResult(SurfaceMesh Mesh, IReadOnlyList<string> Steps);

/// <summary>
/// Puts right the faults that keep a model drawn in CAD from being a closed room, as far as that can be done without guessing: the holes the
/// geometry check finds (<see cref="MeshCheck"/>) and what usually causes the rest.
/// </summary>
/// <remarks>
/// <para>In order, each step on what the last left:</para>
/// <list type="number">
/// <item>Every hole that can be closed is filled (<see cref="HoleFill"/>): a missing floor or ceiling, a wall left out.</item>
/// <item>Corners on open edges, or on edges more than two faces share, within <see cref="SnapM"/> of one another are made one (near misses in the
/// drawing; the hairline faces between them go), and an open edge with a corner of another open edge along it is cut there (a T-junction). Only
/// such edges are touched, so the detail of a closed solid is left alone.</item>
/// <item>Every surface not joined to the room's own (the one with the most area) becomes a free-standing screen, its group renamed
/// "Group screen": furniture standing in the room, a loose sheet such as a table top. With <c>boxes</c>, each closed one (a solid) is replaced by the
/// box round it, so the detail of a chair costs the ray tracer nothing. A screen's face lying on the room's surface (a chair's base on the floor)
/// is left out, as a screen must stand inside the room.</item>
/// <item>The holes that leaves are filled.</item>
/// <item>A flat sheet hanging in the room, open along one side and meeting the walls along the others (where three faces then share an edge), such as a
/// gallery drawn as one face, becomes a screen.</item>
/// </list>
/// <para>Ceiling tiles drawn as faces of their own in a missing ceiling are filled round, so they become part of it; the hole filling comes first
/// for that reason.</para>
/// </remarks>
public static class ModelRepair
{
    /// <summary>How close corners of the room must be to be made one, metres.</summary>
    public const double SnapM = 0.002;

    /// <summary>How far off an open edge a corner may lie and still cut it, metres (as the geometry check's T-junctions).</summary>
    public const double OnEdgeM = MeshCheck.OnEdgeM;

    /// <param name="mesh">The model.</param>
    /// <param name="isScreen">The groups already free-standing screens, which are no part of the room's surface; the groups named as screens if not given.</param>
    /// <param name="boxes">Whether separate solids standing in the room are replaced by their boxes.</param>
    public static RepairResult Repair(SurfaceMesh mesh, Func<string, bool>? isScreen = null, bool boxes = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var given = isScreen ?? SurfaceMesh.LooksLikeScreen;
        bool Screen(string group) => given(group) || SurfaceMesh.LooksLikeScreen(group);
        var steps = new List<string>();

        mesh = FillAll(mesh, Screen, steps);
        mesh = Snap(mesh, Screen, steps);
        mesh = SplitTJunctions(mesh, Screen, steps);
        mesh = Separate(mesh, Screen, boxes, steps);
        mesh = FillAll(mesh, Screen, steps);
        mesh = HangingSheets(mesh, Screen, steps);
        mesh = FillAll(mesh, Screen, steps);
        return new RepairResult(mesh, steps);
    }

    private static SurfaceMesh FillAll(SurfaceMesh mesh, Func<string, bool> screen, List<string> steps)
    {
        var caps = new HoleFill(mesh, screen).PlanAll();
        if (caps.Count == 0) return mesh;
        var named = HoleFill.Named(mesh, caps);
        foreach (var (cap, group) in named) steps.Add($"Filled a hole with \"{group}\", {cap.AreaM2:0.0} m².");
        return HoleFill.Fill(mesh, named);
    }

    /// <summary>The surfaces not joined to the room's as screens (or boxes).</summary>
    private static SurfaceMesh Separate(SurfaceMesh mesh, Func<string, bool> screen, bool boxes, List<string> steps)
    {
        var room = Enumerable.Range(0, mesh.Triangles.Count).Where(i => !screen(mesh.Triangles[i].Group)).ToList();
        var edges = EdgeUses(mesh, room);
        var parent = room.ToDictionary(i => i, i => i);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var uses in edges.Values)
            for (var k = 1; k < uses.Count; k++) parent[Find(uses[k])] = Find(uses[0]);
        var parts = room.GroupBy(Find).Select(g => g.ToList()).ToList();
        if (parts.Count < 2) return mesh;
        var main = parts.MaxBy(p => p.Sum(i => Area(mesh, mesh.Triangles[i])))!;
        var others = parts.Where(p => p != main).ToList();

        var moved = new HashSet<int>(others.SelectMany(p => p));
        var builder = new SurfaceMeshBuilder();
        for (var i = 0; i < mesh.Triangles.Count; i++)
            if (!moved.Contains(i)) Add(builder, mesh, mesh.Triangles[i], mesh.Triangles[i].Group);
        // a screen's face lying on the room's own surface (a chair's base on the floor) goes: a screen must stand inside the room
        var surface = main.Select(i => Corners(mesh, mesh.Triangles[i])).Where(c => (c[1] - c[0]).Cross(c[2] - c[0]).Length > 1e-12).ToList();
        var (solids, sheets, onSurface) = (0, 0, 0);
        void AddScreen(Vec3[] corners, string group)
        {
            if (surface.Any(s => OnTriangle((corners[0] + corners[1] + corners[2]) * (1.0 / 3), s))) { onSurface++; return; }
            builder.AddTriangle(corners[0], corners[1], corners[2], group);
        }
        foreach (var part in others)
        {
            var closed = EdgeUses(mesh, part).Values.All(u => u.Count == 2);
            if (closed) solids++; else sheets++;
            if (boxes && closed)
            {
                var group = part.GroupBy(i => mesh.Triangles[i].Group).MaxBy(g => g.Count())!.Key;
                foreach (var t in Box(part.SelectMany(i => Corners(mesh, mesh.Triangles[i])).ToList())) AddScreen(t, ScreenName(group));
            }
            else
                foreach (var i in part) AddScreen(Corners(mesh, mesh.Triangles[i]), ScreenName(mesh.Triangles[i].Group));
        }
        steps.Add($"Made {others.Count} separate surface{(others.Count == 1 ? "" : "s")} free-standing screens ({solids} solid{(solids == 1 ? "" : "s")}"
            + $"{(boxes && solids > 0 ? ", as boxes" : "")}, {sheets} sheet{(sheets == 1 ? "" : "s")})"
            + (onSurface > 0 ? $", leaving out {onSurface} of their faces that lay on the room's surface." : "."));
        return builder.Build();
    }

    /// <summary>The corners on open or crowded edges (more than two faces) within <see cref="SnapM"/> made one; faces left with two corners the same go.</summary>
    private static SurfaceMesh Snap(SurfaceMesh mesh, Func<string, bool> screen, List<string> steps)
    {
        var room = Enumerable.Range(0, mesh.Triangles.Count).Where(i => !screen(mesh.Triangles[i].Group));
        var used = EdgeUses(mesh, room).Where(kv => kv.Value.Count != 2).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).Distinct().OrderBy(i => i).ToList();
        var to = Enumerable.Range(0, mesh.Vertices.Count).ToArray();
        var cells = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Cell(Vec3 p) => ((long)Math.Floor(p.X / SnapM), (long)Math.Floor(p.Y / SnapM), (long)Math.Floor(p.Z / SnapM));
        var snapped = 0;
        foreach (var i in used)
        {
            var p = mesh.Vertices[i];
            var (cx, cy, cz) = Cell(p);
            var near = -1;
            for (var dx = -1; dx <= 1 && near < 0; dx++)
                for (var dy = -1; dy <= 1 && near < 0; dy++)
                    for (var dz = -1; dz <= 1 && near < 0; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            near = list.FirstOrDefault(k => (mesh.Vertices[k] - p).Length <= SnapM, -1);
            if (near >= 0) { to[i] = near; snapped++; continue; }
            (cells.TryGetValue((cx, cy, cz), out var here) ? here : cells[(cx, cy, cz)] = new List<int>()).Add(i);
        }
        if (snapped == 0) return mesh;
        var builder = new SurfaceMeshBuilder();
        var dropped = 0;
        foreach (var t in mesh.Triangles)
        {
            int a = to[t.A], b = to[t.B], c = to[t.C];
            if (a == b || b == c || a == c) { dropped++; continue; }
            builder.AddTriangle(mesh.Vertices[a], mesh.Vertices[b], mesh.Vertices[c], t.Group);
        }
        steps.Add($"Joined {snapped} corner{(snapped == 1 ? "" : "s")} within {SnapM * 1000:0} mm of another"
            + (dropped > 0 ? $", which took away {dropped} hairline face{(dropped == 1 ? "" : "s")}." : "."));
        return builder.Build();
    }

    /// <summary>Each open edge with a corner of another open edge lying along it, cut there.</summary>
    private static SurfaceMesh SplitTJunctions(SurfaceMesh mesh, Func<string, bool> screen, List<string> steps)
    {
        var count = 0;
        for (var round = 0; round < 50; round++)
        {
            var room = Enumerable.Range(0, mesh.Triangles.Count).Where(i => !screen(mesh.Triangles[i].Group)).ToList();
            var open = EdgeUses(mesh, room).Where(kv => kv.Value.Count == 1).ToList();
            if (open.Count == 0) break;
            var corners = open.SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).Distinct().ToList();
            // at most one cut per face a round, at the corner nearest the edge's start
            var cuts = new Dictionary<int, ((int, int) Edge, int At)>();
            foreach (var (edge, uses) in open)
            {
                if (cuts.ContainsKey(uses[0])) continue;
                Vec3 a = mesh.Vertices[edge.Item1], b = mesh.Vertices[edge.Item2];
                var along = b - a;
                var length2 = along.Dot(along);
                if (length2 < 1e-12) continue;
                var best = -1;
                var bestAt = 1.0;
                foreach (var c in corners)
                {
                    if (c == edge.Item1 || c == edge.Item2) continue;
                    var at = (mesh.Vertices[c] - a).Dot(along) / length2;
                    if (at <= 1e-6 || at >= bestAt || at >= 1 - 1e-6) continue;
                    if ((a + along * at - mesh.Vertices[c]).Length > OnEdgeM) continue;
                    (best, bestAt) = (c, at);
                }
                if (best >= 0) cuts[uses[0]] = (edge, best);
            }
            if (cuts.Count == 0) break;
            var builder = new SurfaceMeshBuilder();
            for (var i = 0; i < mesh.Triangles.Count; i++)
            {
                var t = mesh.Triangles[i];
                if (!cuts.TryGetValue(i, out var cut)) { Add(builder, mesh, t, t.Group); continue; }
                var ring = new[] { t.A, t.B, t.C };
                var k = Enumerable.Range(0, 3).First(j => Key(ring[j], ring[(j + 1) % 3]) == cut.Edge);
                int p = ring[k], q = ring[(k + 1) % 3], r = ring[(k + 2) % 3];
                builder.AddTriangle(mesh.Vertices[p], mesh.Vertices[cut.At], mesh.Vertices[r], t.Group);
                builder.AddTriangle(mesh.Vertices[cut.At], mesh.Vertices[q], mesh.Vertices[r], t.Group);
            }
            count += cuts.Count;
            mesh = builder.Build();
        }
        if (count > 0) steps.Add($"Cut {count} open edge{(count == 1 ? "" : "s")} at a corner lying along {(count == 1 ? "it" : "them")} (a T-junction).");
        return mesh;
    }

    /// <summary>
    /// Flat sheets hanging in the room as screens: the faces reached from an open edge across edges between faces in one plane, if they all lie in
    /// that plane and every edge round them is either open or shared by more than two faces (where the sheet meets a wall).
    /// </summary>
    private static SurfaceMesh HangingSheets(SurfaceMesh mesh, Func<string, bool> screen, List<string> steps)
    {
        var room = Enumerable.Range(0, mesh.Triangles.Count).Where(i => !screen(mesh.Triangles[i].Group) && Area(mesh, mesh.Triangles[i]) > 1e-9).ToList();
        var edges = EdgeUses(mesh, room);
        var normal = room.ToDictionary(i => i, i => Normal(mesh, mesh.Triangles[i]));
        var taken = new HashSet<int>();
        var sheets = new List<List<int>>();
        foreach (var start in edges.Where(kv => kv.Value.Count == 1).Select(kv => kv.Value[0]))
        {
            if (taken.Contains(start)) continue;
            var sheet = new List<int>();
            var seen = new HashSet<int> { start };
            var queue = new Queue<int>(new[] { start });
            var flat = true;
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                sheet.Add(i);
                foreach (var e in Edges(mesh.Triangles[i]))
                {
                    var uses = edges[e];
                    if (uses.Count == 1 || uses.Count > 2) continue; // the sheet's edge: open, or where it meets a wall
                    var n = uses[0] == i ? uses[1] : uses[0];
                    if (seen.Contains(n)) continue;
                    if (Math.Abs(normal[n].Dot(normal[start])) < 0.9995) { flat = false; continue; } // it bends: no hanging sheet
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
            taken.UnionWith(sheet);
            if (flat && sheet.Sum(i => Area(mesh, mesh.Triangles[i])) >= HoleFill.MinimumAreaM2 && sheet.SelectMany(i => Edges(mesh.Triangles[i])).Any(e => edges[e].Count > 2)) sheets.Add(sheet);
        }
        if (sheets.Count == 0) return mesh;
        var moved = sheets.SelectMany(s => s).ToHashSet();
        var builder = new SurfaceMeshBuilder();
        for (var i = 0; i < mesh.Triangles.Count; i++)
            Add(builder, mesh, mesh.Triangles[i], moved.Contains(i) ? ScreenName(mesh.Triangles[i].Group) : mesh.Triangles[i].Group);
        var area = moved.Sum(i => Area(mesh, mesh.Triangles[i]));
        steps.Add($"Made {sheets.Count} flat sheet{(sheets.Count == 1 ? "" : "s")} hanging in the room ({area:0.0} m², such as a gallery drawn as one face) free-standing screens.");
        return builder.Build();
    }

    private static string ScreenName(string group) => SurfaceMesh.LooksLikeScreen(group) ? group : $"{group} screen";

    private static Dictionary<(int, int), List<int>> EdgeUses(SurfaceMesh mesh, IEnumerable<int> triangles)
    {
        var edges = new Dictionary<(int, int), List<int>>();
        foreach (var i in triangles)
            foreach (var e in Edges(mesh.Triangles[i]))
                (edges.TryGetValue(e, out var list) ? list : edges[e] = new List<int>(2)).Add(i);
        return edges;
    }

    private static IEnumerable<(int, int)> Edges(MeshTriangle t) => new[] { Key(t.A, t.B), Key(t.B, t.C), Key(t.C, t.A) };

    private static (int, int) Key(int a, int b) => (Math.Min(a, b), Math.Max(a, b));

    private static Vec3[] Corners(SurfaceMesh mesh, MeshTriangle t) => new[] { mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C] };

    private static double Area(SurfaceMesh mesh, MeshTriangle t) => (mesh.Vertices[t.B] - mesh.Vertices[t.A]).Cross(mesh.Vertices[t.C] - mesh.Vertices[t.A]).Length / 2;

    private static Vec3 Normal(SurfaceMesh mesh, MeshTriangle t) => (mesh.Vertices[t.B] - mesh.Vertices[t.A]).Cross(mesh.Vertices[t.C] - mesh.Vertices[t.A]).Normalized();

    private static void Add(SurfaceMeshBuilder builder, SurfaceMesh mesh, MeshTriangle t, string group) =>
        builder.AddTriangle(mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C], group);

    /// <summary>The twelve triangles of the box round the corners.</summary>
    private static IEnumerable<Vec3[]> Box(List<Vec3> corners)
    {
        var lo = new Vec3(corners.Min(v => v.X), corners.Min(v => v.Y), corners.Min(v => v.Z));
        var hi = new Vec3(corners.Max(v => v.X), corners.Max(v => v.Y), corners.Max(v => v.Z));
        Vec3 C(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
        var quads = new[]
        {
            (C(0, 0, 0), C(1, 0, 0), C(1, 1, 0), C(0, 1, 0)), (C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1)),
            (C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1)), (C(0, 1, 0), C(1, 1, 0), C(1, 1, 1), C(0, 1, 1)),
            (C(0, 0, 0), C(0, 1, 0), C(0, 1, 1), C(0, 0, 1)), (C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1)),
        };
        foreach (var (a, b, c, d) in quads)
        {
            yield return new[] { a, b, c };
            yield return new[] { a, c, d };
        }
    }

    /// <summary>Whether a point lies on a triangle (within <see cref="SnapM"/> of its plane, and inside it).</summary>
    private static bool OnTriangle(Vec3 p, Vec3[] t)
    {
        var normal = (t[1] - t[0]).Cross(t[2] - t[0]);
        var twice = normal.Length;
        if (Math.Abs(normal.Dot(p - t[0])) / twice > SnapM) return false;
        // the point's barycentric weights, each from the area it makes with an edge, all of the triangle's sign
        for (var k = 0; k < 3; k++)
            if ((t[(k + 1) % 3] - t[k]).Cross(p - t[k]).Dot(normal) < -1e-9 * twice) return false;
        return true;
    }
}
