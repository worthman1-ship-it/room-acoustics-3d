namespace RoomAcoustics.Core;

/// <summary>What is wrong at one place in a model that is not a closed surface.</summary>
public enum MeshFaultKind
{
    /// <summary>A run of edges each used by one face only: a gap in the surface.</summary>
    Hole,
    /// <summary>Edges used by more than two faces: a face inside the room, a doubled face, or a fin.</summary>
    Crowded,
    /// <summary>A surface not joined to the main one, such as a solid standing inside the room.</summary>
    Separate,
}

/// <summary>One fault: the edges that show where it is (for a separate part, its bounding box), the groups of the faces there, and how it might be put right.</summary>
public sealed record MeshFault(MeshFaultKind Kind, IReadOnlyList<(Vec3 A, Vec3 B)> Edges, IReadOnlyList<string> Groups, string Hint)
{
    public double LengthM => Edges.Sum(e => (e.B - e.A).Length);
}

/// <summary>The edges used by one face, and by more than two, the number of separate surfaces, and the faults, the largest first (at most <see cref="MeshCheck.MaxFaults"/>).</summary>
public sealed record MeshCheckResult(int OpenEdges, int CrowdedEdges, int Parts, IReadOnlyList<MeshFault> Faults);

/// <summary>
/// Finds where an imported model fails to be the one closed surface a room needs, so it can be put right in the CAD program. Open edges that join up are one
/// hole; a hole is checked for a corner of another face lying along one of its edges (a T-junction: the long edge must be split there) and for corners that
/// nearly meet but were not welded (a small gap). The groups taken as free-standing screens are left out, as they need not be closed.
/// </summary>
public static class MeshCheck
{
    public const int MaxFaults = 100;

    /// <summary>Corners closer than this, but not welded, are reported as a gap.</summary>
    public const double GapM = 0.05;

    /// <summary>How far off an edge a corner may lie and still be taken to be on it (a T-junction).</summary>
    public const double OnEdgeM = 0.001;

    public static MeshCheckResult Check(SurfaceMesh mesh, Func<string, bool>? isScreen = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var v = mesh.Vertices;
        // the faces a room is built from: as SurfaceMesh.ToRoom takes them
        var triangles = mesh.Faces().Where(t => isScreen?.Invoke(t.Group) != true).ToList();

        var edges = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < triangles.Count; i++)
        {
            var t = triangles[i];
            foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            {
                var key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var uses)) edges[key] = uses = new List<int>(2);
                uses.Add(i);
            }
        }
        var open = edges.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList();
        var crowded = edges.Where(kv => kv.Value.Count > 2).Select(kv => kv.Key).ToList();
        var used = triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().ToList();
        var openCorners = open.SelectMany(e => new[] { e.Item1, e.Item2 }).ToHashSet();

        var faults = new List<MeshFault>();
        var holes = new Dictionary<MeshFault, List<(int, int)>>(ReferenceEqualityComparer.Instance);
        foreach (var run in Runs(open))
        {
            var hole = new MeshFault(MeshFaultKind.Hole, run.Select(e => (v[e.Item1], v[e.Item2])).ToList(), GroupsOf(run, edges, triangles), "");
            faults.Add(hole);
            holes[hole] = run;
        }
        foreach (var run in Runs(crowded))
        {
            var groups = GroupsOf(run, edges, triangles);
            var most = run.Max(e => edges[e].Count);
            faults.Add(new MeshFault(MeshFaultKind.Crowded, run.Select(e => (v[e.Item1], v[e.Item2])).ToList(), groups,
                $"Up to {most} faces meet along here, where a closed surface has two: a face inside the room, a face drawn twice, or a fin. Delete the extra face, or make its group free-standing screens."));
        }

        // the room is the part with the most area, whatever the number of faces it was cut into
        double Area(MeshTriangle t) => (v[t.B] - v[t.A]).Cross(v[t.C] - v[t.A]).Length / 2;
        var parts = Parts(triangles, edges);
        foreach (var part in parts.OrderByDescending(p => p.Sum(i => Area(triangles[i]))).Skip(1))
        {
            var corners = part.SelectMany(i => new[] { v[triangles[i].A], v[triangles[i].B], v[triangles[i].C] }).ToList();
            var groups = part.Select(i => triangles[i].Group).Distinct().ToList();
            faults.Add(new MeshFault(MeshFaultKind.Separate, Box(corners), groups,
                $"A separate surface of {part.Count} faces, not joined to the room's: a solid standing in the room, or a stray face. A room must be one closed surface; delete it, or make its group free-standing screens."));
        }

        // the hints are worked out for the faults listed only, as looking for T-junctions takes time in a big model
        var listed = faults.OrderBy(f => f.Kind == MeshFaultKind.Separate ? 1 : 0).ThenByDescending(f => f.LengthM).Take(MaxFaults)
            .Select(f => holes.TryGetValue(f, out var run) ? f with { Hint = HoleHint(run, v, used, openCorners) } : f).ToList();
        return new MeshCheckResult(open.Count, crowded.Count, parts.Count, listed);
    }

    /// <summary>The edges in runs that share corners.</summary>
    private static List<List<(int, int)>> Runs(List<(int, int)> edges)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (a, b) in edges) { parent.TryAdd(a, a); parent.TryAdd(b, b); parent[Find(a)] = Find(b); }
        return edges.GroupBy(e => Find(e.Item1)).Select(g => g.ToList()).ToList();
    }

    /// <summary>The faces joined by shared edges, as lists of indices into <paramref name="triangles"/>.</summary>
    private static List<List<int>> Parts(List<MeshTriangle> triangles, Dictionary<(int, int), List<int>> edges)
    {
        var parent = Enumerable.Range(0, triangles.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var uses in edges.Values)
            for (var k = 1; k < uses.Count; k++) parent[Find(uses[k])] = Find(uses[0]);
        return Enumerable.Range(0, triangles.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
    }

    private static List<string> GroupsOf(List<(int, int)> run, Dictionary<(int, int), List<int>> edges, List<MeshTriangle> triangles) =>
        run.SelectMany(e => edges[e]).Select(i => triangles[i].Group).Distinct().ToList();

    private static string HoleHint(List<(int, int)> run, IReadOnlyList<Vec3> v, List<int> used, HashSet<int> openCorners)
    {
        // a corner of another face lying along one of the hole's edges: the faces on the two sides of it are split differently
        var ends = run.SelectMany(e => new[] { v[e.Item1], v[e.Item2] }).ToList();
        var lo = new Vec3(ends.Min(p => p.X) - OnEdgeM, ends.Min(p => p.Y) - OnEdgeM, ends.Min(p => p.Z) - OnEdgeM);
        var hi = new Vec3(ends.Max(p => p.X) + OnEdgeM, ends.Max(p => p.Y) + OnEdgeM, ends.Max(p => p.Z) + OnEdgeM);
        var near = used.Where(c => v[c].X >= lo.X && v[c].X <= hi.X && v[c].Y >= lo.Y && v[c].Y <= hi.Y && v[c].Z >= lo.Z && v[c].Z <= hi.Z).ToList();
        foreach (var (a, b) in run)
        {
            var along = v[b] - v[a];
            var length = along.Length;
            foreach (var c in near)
            {
                if (c == a || c == b) continue;
                var t = (v[c] - v[a]).Dot(along) / (length * length);
                if (t <= 0 || t >= 1) continue;
                if ((v[a] + along * t - v[c]).Length <= OnEdgeM)
                    return $"A corner of another face lies along an edge here (a T-junction, at {Where(v[c])}): the faces each side are split differently. Split the long edge at that corner, or join the faces.";
            }
        }
        // two corners of the hole close together but not joined
        var corners = run.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct().ToList();
        var nearest = double.PositiveInfinity;
        Vec3 at = default;
        foreach (var a in corners)
            foreach (var b in openCorners)
            {
                if (a == b) continue;
                var d = (v[a] - v[b]).Length;
                if (d < nearest) { nearest = d; at = v[a]; }
            }
        if (nearest <= GapM)
            return $"Two corners {nearest * 1000:0.#} mm apart are not joined (at {Where(at)}): snap them together in the CAD program.";
        return "These edges have a face on one side only: a face is missing, or the faces beside the gap do not meet.";
    }

    private static string Where(Vec3 p) => $"x {p.X:0.00}, y {p.Y:0.00}, z {p.Z:0.00} m";

    /// <summary>The twelve edges of the box round some points.</summary>
    private static List<(Vec3, Vec3)> Box(List<Vec3> points)
    {
        var lo = new Vec3(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        var hi = new Vec3(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
        Vec3 C(int i) => new((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
        var box = new List<(Vec3, Vec3)>();
        for (var i = 0; i < 8; i++)
            foreach (var bit in new[] { 1, 2, 4 })
                if ((i & bit) == 0) box.Add((C(i), C(i | bit)));
        return box;
    }
}
