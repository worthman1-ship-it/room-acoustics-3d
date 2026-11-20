using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>One triangle of an imported model, by vertex index, and the surface group (material, layer) it belongs to.</summary>
public sealed record MeshTriangle(int A, int B, int C, string Group);

/// <summary>
/// A triangle mesh read from a CAD or modelling file, in metres, z up. Every importer produces one, and
/// <see cref="ToRoom"/> turns it into a <see cref="Room3D"/>: each surface group (an OBJ material, a DXF or DWG
/// layer) gets one acoustic finish.
/// </summary>
public sealed class SurfaceMesh
{
    public SurfaceMesh(IReadOnlyList<Vec3> vertices, IReadOnlyList<MeshTriangle> triangles)
    {
        Vertices = vertices;
        Triangles = triangles;
    }

    public IReadOnlyList<Vec3> Vertices { get; }

    public IReadOnlyList<MeshTriangle> Triangles { get; }

    /// <summary>The surface groups, in the order they first appear. Assign a finish to each.</summary>
    public IReadOnlyList<string> Groups => Triangles.Select(t => t.Group).Distinct().ToList();

    /// <summary>
    /// The same mesh with the triangles of <paramref name="group"/> given to <paramref name="to"/>: a new name renames the group, another group's name
    /// merges the two.
    /// </summary>
    public SurfaceMesh WithGroupRenamed(string group, string to) =>
        new(Vertices, Triangles.Select(t => t.Group == group ? t with { Group = to } : t).ToList());

    /// <summary>The same mesh without the triangles of <paramref name="group"/>, and without the corners only they used.</summary>
    public SurfaceMesh WithoutGroup(string group)
    {
        var kept = Triangles.Where(t => t.Group != group).ToList();
        var used = kept.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Order().ToList();
        var index = used.Select((old, i) => (old, i)).ToDictionary(p => p.old, p => p.i);
        return new(used.Select(i => Vertices[i]).ToList(), kept.Select(t => t with { A = index[t.A], B = index[t.B], C = index[t.C] }).ToList());
    }

    /// <summary>Area of each group, m2, to help decide which finish goes where.</summary>
    public IReadOnlyDictionary<string, double> GroupAreasM2() =>
        Triangles.GroupBy(t => t.Group).ToDictionary(g => g.Key, g => g.Sum(TriangleArea));

    /// <summary>
    /// Builds a room from the mesh. <paramref name="finishFor"/> gives the finish of each surface group. Degenerate
    /// triangles are dropped. The mesh must be a closed surface (see <see cref="Room3D"/>), and the error says how many
    /// edges are unmatched if it is not. The groups <paramref name="isScreen"/> picks are free-standing screens inside it instead, and need not be closed;
    /// <paramref name="moreScreens"/> are further screens (drawn rather than modelled).
    /// </summary>
    public Room3D ToRoom(Func<string, SurfaceFinish> finishFor, Func<string, bool>? isScreen = null, IEnumerable<PlanarFace>? moreScreens = null)
    {
        ArgumentNullException.ThrowIfNull(finishFor);
        var finishes = Groups.ToDictionary(g => g, finishFor);
        var screenGroups = Groups.Where(g => isScreen?.Invoke(g) == true).ToHashSet();

        var faces = new List<PlanarFace>();
        var screens = new List<PlanarFace>();
        var counts = new Dictionary<string, int>();
        foreach (var t in Faces())
        {
            var number = counts[t.Group] = counts.GetValueOrDefault(t.Group) + 1;
            (screenGroups.Contains(t.Group) ? screens : faces).Add(new PlanarFace($"{t.Group} {number}", new[] { Vertices[t.A], Vertices[t.B], Vertices[t.C] }, finishes[t.Group]));
        }
        if (faces.Count == 0) throw new ArgumentException("The model has no surfaces other than screens.");
        return new Room3D(faces, moreScreens is null ? screens : screens.Concat(moreScreens));
    }

    /// <summary>The margin, metres, between a model taken as outdoors and the open box round it, and the least height of the box over the model's lowest point.</summary>
    public const double OpenAirMarginM = 1, OpenAirHeadroomM = 20;

    /// <summary>
    /// The model taken as outdoors: every surface stands in the open as a two-sided screen (sound reflects off either side, and they need not be closed), inside
    /// a box <see cref="OpenAirMarginM"/> clear of the model on every side and as far below its lowest point (and at least <see cref="OpenAirHeadroomM"/> high
    /// over that), whose faces take every sound that reaches them,
    /// as the open sky and the ground beyond the model do. Sound bends round a surface's free edges as round a screen's; a closed solid (a building) has none,
    /// so nothing bends round it. The volume and surface area are the box's, so the statistical estimates mean nothing here.
    /// </summary>
    public Room3D ToOpenAir(Func<string, SurfaceFinish> finishFor, IEnumerable<PlanarFace>? moreScreens = null)
    {
        ArgumentNullException.ThrowIfNull(finishFor);
        var finishes = Groups.ToDictionary(g => g, finishFor);
        var screens = new List<PlanarFace>();
        var counts = new Dictionary<string, int>();
        foreach (var t in Faces())
        {
            var number = counts[t.Group] = counts.GetValueOrDefault(t.Group) + 1;
            screens.Add(new PlanarFace($"{t.Group} {number}", new[] { Vertices[t.A], Vertices[t.B], Vertices[t.C] }, finishes[t.Group]));
        }
        if (screens.Count == 0) throw new ArgumentException("The model has no surfaces.");
        var m = OpenAirMarginM;
        double x0 = Vertices.Min(v => v.X) - m, y0 = Vertices.Min(v => v.Y) - m, z0 = Vertices.Min(v => v.Z) - m;
        // at least 20 m of air over the lowest point, so ground alone has room above it for sources and receivers
        double x1 = Vertices.Max(v => v.X) + m, y1 = Vertices.Max(v => v.Y) + m, z1 = Math.Max(Vertices.Max(v => v.Z) + m, Vertices.Min(v => v.Z) + OpenAirHeadroomM);
        var open = new SurfaceFinish(new AbsorptionMaterial("Open air", "Open", Enumerable.Repeat((double?)1.0, 8).ToArray()), OctaveSpectrum.Flat(0));
        var box = Room3D.Extrude(new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) }, z1 - z0, open, open, open).Faces
            .Select(f => new PlanarFace("Open air", f.Vertices.Select(v => new Vec3(v.X, v.Y, v.Z + z0)).ToList(), open));
        return new Room3D(box, moreScreens is null ? screens : screens.Concat(moreScreens));
    }

    /// <summary>Whether a group's name says it is free-standing screens ("Screens", "Desk screen", "BARRIER"...), the groups an import treats as screens to begin with.</summary>
    public static bool LooksLikeScreen(string group) =>
        group.Contains("screen", StringComparison.OrdinalIgnoreCase) || group.Contains("barrier", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The triangles a room is built from: none with a repeated corner, and no sliver. A sliver (three corners in a line, as SketchUp leaves where a face has
    /// a corner along its edge, so that a wall meets a ceiling drawn in pieces) has no area, but it is what joins the face's long edge to the short ones
    /// beside it; so rather than being dropped, which would open the surface there, it is folded into the triangle across its long edge, which is cut in two
    /// at the sliver's middle corner. The surface stays closed, and every triangle has an area.
    /// </summary>
    internal List<MeshTriangle> Faces()
    {
        var triangles = Triangles.Where(t => t.A != t.B && t.B != t.C && t.A != t.C).ToList();
        bool IsSliver(MeshTriangle t) => TriangleArea(t) < 1e-9;
        for (var pass = 0; pass < 100; pass++)
        {
            var byEdge = new Dictionary<(int, int), List<int>>();
            for (var i = 0; i < triangles.Count; i++)
            {
                var t = triangles[i];
                foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                {
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new List<int>();
                    list.Add(i);
                }
            }
            var touched = new HashSet<int>();
            var folded = false;
            for (var i = 0; i < triangles.Count; i++)
            {
                var t = triangles[i];
                if (touched.Contains(i) || !IsSliver(t)) continue;
                // the long edge, and the corner between its ends
                var corners = new[] { t.A, t.B, t.C };
                var k = Enumerable.Range(0, 3).MaxBy(e => (Vertices[corners[(e + 1) % 3]] - Vertices[corners[e]]).Length);
                int a = corners[k], b = corners[(k + 1) % 3], middle = corners[(k + 2) % 3];
                var across = byEdge[(Math.Min(a, b), Math.Max(a, b))].Where(j => j != i && !touched.Contains(j)).ToList();
                if (across.Count != 1) continue;
                var j = across[0];
                var n = triangles[j];
                var m = new[] { n.A, n.B, n.C };
                var e = Enumerable.Range(0, 3).FirstOrDefault(x => (m[x] == a && m[(x + 1) % 3] == b) || (m[x] == b && m[(x + 1) % 3] == a), -1);
                if (e < 0) continue;
                // the triangle across, cut at the middle corner, keeping its winding and its group; the sliver goes
                int u = m[e], v = m[(e + 1) % 3], c = m[(e + 2) % 3];
                triangles[j] = new MeshTriangle(u, middle, c, n.Group);
                triangles[i] = new MeshTriangle(middle, v, c, n.Group);
                touched.Add(i); touched.Add(j);
                folded = true;
            }
            if (!folded) break;
        }
        return triangles.Where(t => t.A != t.B && t.B != t.C && t.A != t.C && TriangleArea(t) >= 1e-9).ToList();
    }

    private double TriangleArea(MeshTriangle t) =>
        (Vertices[t.B] - Vertices[t.A]).Cross(Vertices[t.C] - Vertices[t.A]).Length / 2;
}

/// <summary>
/// Collects triangles and welds corners that lie within a tolerance of each other, so that triangles from separate
/// faces of a model meet at shared vertices, as <see cref="Room3D"/> needs to find the closed surface.
/// </summary>
public sealed class SurfaceMeshBuilder
{
    private readonly double _tolerance;
    private readonly List<Vec3> _vertices = new();
    private readonly List<MeshTriangle> _triangles = new();
    private readonly Dictionary<(long, long, long), List<int>> _cells = new();

    /// <param name="weldToleranceM">Corners closer than this are the same vertex. The default is 0.1 mm.</param>
    public SurfaceMeshBuilder(double weldToleranceM = 1e-4)
    {
        if (weldToleranceM <= 0) throw new ArgumentOutOfRangeException(nameof(weldToleranceM), "The weld tolerance must be positive.");
        _tolerance = weldToleranceM;
    }

    public int TriangleCount => _triangles.Count;

    public void AddTriangle(Vec3 a, Vec3 b, Vec3 c, string group) =>
        _triangles.Add(new MeshTriangle(Weld(a), Weld(b), Weld(c), group));

    /// <summary>A planar quadrilateral, split along its first diagonal. A repeated last corner makes it a triangle.</summary>
    public void AddQuad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, string group)
    {
        AddTriangle(a, b, c, group);
        if (Distance(c, d) > _tolerance) AddTriangle(a, c, d, group);
    }

    public SurfaceMesh Build() => new(_vertices.ToList(), _triangles.ToList());

    private static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

    private (long, long, long) Cell(Vec3 p) =>
        ((long)Math.Floor(p.X / _tolerance), (long)Math.Floor(p.Y / _tolerance), (long)Math.Floor(p.Z / _tolerance));

    private int Weld(Vec3 p)
    {
        var (cx, cy, cz) = Cell(p);
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                    if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var candidates))
                        foreach (var index in candidates)
                            if (Distance(_vertices[index], p) <= _tolerance)
                                return index;

        _vertices.Add(p);
        var cell = (cx, cy, cz);
        if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<int>();
        list.Add(_vertices.Count - 1);
        return _vertices.Count - 1;
    }
}
