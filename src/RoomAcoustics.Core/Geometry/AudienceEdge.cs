namespace RoomAcoustics.Core;

/// <summary>
/// How much of a seating area's edge is open to the room, for the edge effect of audience absorption (a 0.5 m strip along the exposed edge adds to the
/// area the absorption acts over). The edge of a surface group is its outline: the edges its triangles do not share with one another. An outline edge is
/// exposed unless the face beside it rises more than <see cref="RiseM"/> above it, as a wall or a high riser does; one beside an aisle, a gangway at the
/// same level, the stage or a step down is exposed, and so is one with nothing beside it (an open edge of the model).
/// </summary>
public static class AudienceEdge
{
    /// <summary>How far the face beside an edge must rise above it, metres, to shield it: more than a seat back's step, less than a wall.</summary>
    public const double RiseM = 0.3;

    /// <summary>The length, metres, of the outline of <paramref name="group"/> that is open to the room.</summary>
    public static double ExposedLengthM(SurfaceMesh mesh, string group)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var v = mesh.Vertices;
        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        static IEnumerable<(int, int)> Edges(MeshTriangle t) => new[] { Key(t.A, t.B), Key(t.B, t.C), Key(t.C, t.A) };

        // the edges of the group's triangles, with how many of them use each
        var uses = new Dictionary<(int, int), int>();
        foreach (var t in mesh.Triangles.Where(t => t.Group == group))
            foreach (var e in Edges(t)) uses[e] = uses.GetValueOrDefault(e) + 1;

        // the other triangles beside each outline edge
        var outline = uses.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();
        var beside = outline.ToDictionary(e => e, _ => new List<MeshTriangle>());
        foreach (var t in mesh.Triangles.Where(t => t.Group != group))
            foreach (var e in Edges(t))
                if (beside.TryGetValue(e, out var list)) list.Add(t);

        var exposed = 0.0;
        foreach (var (a, b) in outline)
        {
            var top = Math.Max(v[a].Z, v[b].Z);
            var shielded = beside[(a, b)].Any(t => new[] { t.A, t.B, t.C }.Any(i => v[i].Z > top + RiseM));
            if (!shielded) exposed += (v[a] - v[b]).Length;
        }
        return exposed;
    }
}
