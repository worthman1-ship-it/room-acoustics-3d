namespace RoomAcoustics.Core;

/// <summary>
/// The direct sound that reaches a receiver round a corner of the room itself: when the room's walls stand between the source and the receiver, sound still
/// bends round the room's outward-pointing edges (a re-entrant corner of an L-shaped room, the front of a balcony, the corner of a pillar or a stair core).
/// Each such edge that both can see gives a path over the shortest route round it, cut by the same thin-barrier formula the screens use
/// (<see cref="ScreenDiffraction.InsertionLossDb"/>, Kurze and Anderson's form of Maekawa's curve, at most <see cref="ScreenDiffraction.MaxAttenuationDb"/>),
/// and spread over the whole route's length. The paths' energies add.
/// </summary>
/// <remarks>
/// It is a single diffraction: a path round two corners is not found. Reflections a corner hides bend round it too, up to <see cref="MaxReflections"/> of them
/// before or after the bend (see <see cref="ImageSourceEnumerator.EnumerateRoundCorners"/>).
/// A wedge diffracts less than a thin screen into most of its shadow, so this somewhat overstates the sound deep behind a corner, as the 20 dB limit of a
/// single barrier does.
/// </remarks>
public sealed class CornerDiffraction
{
    /// <summary>One edge sound can bend round: its ends, the unit vector out of the corner into the room (between the two faces' inward normals) and those two faces.</summary>
    public sealed record Edge(Vec3 A, Vec3 B, Vec3 Out, int Face1, int Face2);

    /// <summary>The most reflections a path round a corner takes, all before the bend or all after it (they are found by images, whose number grows with the order).</summary>
    public const int MaxReflections = 2;

    /// <summary>One way round a corner: the point on the edge it turns at, its whole length and how much longer it is than the straight line.</summary>
    public sealed record Path(Vec3 Turn, double LengthM, double ExtraM);

    private const double Offset = 0.001; // the turning point is taken this far out of the corner, so the legs do not graze its faces

    /// <summary>The room's outward-pointing edges: those its closed surface folds away from the room at (none in a convex room).</summary>
    public IReadOnlyList<Edge> Edges { get; }

    private readonly Room3D _room;

    public CornerDiffraction(Room3D room)
    {
        ArgumentNullException.ThrowIfNull(room);
        _room = room;
        Edges = room.IsConvex ? Array.Empty<Edge>() : FindEdges(room);
    }

    private static List<Edge> FindEdges(Room3D room)
    {
        (long, long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));
        var users = new Dictionary<((long, long, long), (long, long, long)), (Vec3 A, Vec3 B, List<int> Faces)>();
        for (var f = 0; f < room.BoundaryFaceCount; f++)
        {
            var v = room.Faces[f].Vertices;
            for (var k = 0; k < v.Count; k++)
            {
                Vec3 p = v[k], q = v[(k + 1) % v.Count];
                var (kp, kq) = (Key(p), Key(q));
                var key = kp.CompareTo(kq) <= 0 ? (kp, kq) : (kq, kp);
                if (!users.TryGetValue(key, out var entry)) users[key] = entry = (p, q, new List<int>());
                entry.Faces.Add(f);
            }
        }

        var edges = new List<Edge>();
        foreach (var (a, b, faces) in users.Values)
        {
            if (faces.Count != 2) continue;
            RoomFace f1 = room.Faces[faces[0]], f2 = room.Faces[faces[1]];
            if (f1.InwardNormal.Dot(f2.InwardNormal) > 1 - 1e-6) continue; // two faces of one flat surface
            // the second face folds away from the room when its far corner lies behind the first face's plane
            var far = f2.Vertices.OrderByDescending(p => ((p - a).Cross(b - a)).Length).First();
            if (f1.InwardNormal.Dot(far - a) >= -1e-6) continue;
            edges.Add(new Edge(a, b, (f1.InwardNormal + f2.InwardNormal).Normalized(), faces[0], faces[1]));
        }
        return edges;
    }

    /// <summary>The ways round the room's corners from <paramref name="from"/> to <paramref name="to"/>; none when the straight line between them is clear of the walls.</summary>
    public IReadOnlyList<Path> Paths(Vec3 from, Vec3 to)
    {
        if (Edges.Count == 0 || _room.IsClearOfWalls(from, to)) return Array.Empty<Path>();
        var straight = (to - from).Length;
        var paths = new List<Path>();
        foreach (var edge in Edges)
        {
            if (TurnPoint(edge, from, to) is not { } turn) continue;
            if (!_room.IsClearOfWalls(from, turn) || !_room.IsClearOfWalls(turn, to)) continue;
            var route = (turn - from).Length + (to - turn).Length;
            paths.Add(new Path(turn, route, Math.Max(0, route - straight)));
        }
        return paths;
    }

    /// <summary>
    /// Where the shortest route from <paramref name="from"/> to <paramref name="to"/> over an edge turns (just out of the corner): unfolded about the edge the route is
    /// straight. Null when it would turn beyond this piece of edge (a split edge's other pieces have their own).
    /// </summary>
    private static Vec3? TurnPoint(Edge edge, Vec3 from, Vec3 to)
    {
        var length = (edge.B - edge.A).Length;
        var u = (edge.B - edge.A) * (1 / length);
        double tFrom = (from - edge.A).Dot(u), tTo = (to - edge.A).Dot(u);
        double dFrom = (from - (edge.A + u * tFrom)).Length, dTo = (to - (edge.A + u * tTo)).Length;
        if (dFrom + dTo < 1e-9) return null;
        var t = (tFrom * dTo + tTo * dFrom) / (dFrom + dTo);
        if (t < 0 || t >= length) return null;
        return edge.A + u * t + edge.Out * Offset;
    }

    /// <summary>
    /// Where a reflected path bends round an edge, for <see cref="ImageSourceEnumerator.EnumerateRoundCorners"/>: the route from <paramref name="image"/> (an image of
    /// one end, seen through its reflections) to the other end <paramref name="to"/>, turning at the edge. Null unless the edge's corner hides <paramref name="to"/> from
    /// that image, that is unless the real part of the straight line, from <paramref name="near"/> (where it leaves the last face it reflects from) to
    /// <paramref name="to"/>, passes through one of the corner's own two faces. Out of the shadow the reflection's own path is the sound there, and the bend adds
    /// nothing. (A wall drawn as several faces shadows only through the ones that meet at the edge.)
    /// </summary>
    public Vec3? ReflectedTurn(Edge edge, Vec3 image, Vec3 to, Vec3 near) =>
        Crosses(edge.Face1, near, to) || Crosses(edge.Face2, near, to) ? TurnPoint(edge, image, to) : null;

    private bool Crosses(int face, Vec3 from, Vec3 to)
    {
        var f = _room.Faces[face];
        double p = f.InwardNormal.Dot(from - f.Vertices[0]), q = f.InwardNormal.Dot(to - f.Vertices[0]);
        if (p * q >= 0) return false;
        return _room.FaceContains(face, from + (to - from) * (p / (p - q)));
    }

    /// <summary>The share of energy a path round a corner keeps, at a wavelength: the thin barrier's insertion loss at its Fresnel number, at most 20 dB.</summary>
    public static double Factor(Path path, double wavelengthM) =>
        Math.Pow(10, -Math.Min(ScreenDiffraction.InsertionLossDb(2 * path.ExtraM / wavelengthM), ScreenDiffraction.MaxAttenuationDb) / 10);
}
