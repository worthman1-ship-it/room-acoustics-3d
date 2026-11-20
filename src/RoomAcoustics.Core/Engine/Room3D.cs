using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>
/// The acoustic finish of one face: an absorption spectrum (from an
/// <see cref="AbsorptionMaterial"/>) and an optional per-band scattering
/// coefficient. Scattering is the fraction of reflected energy sent back
/// diffusely (Lambert's cosine law) rather than specularly; it defaults to
/// zero, which is purely specular reflection.
/// </summary>
/// <summary>
/// What a surface does to sound: its absorption, its scattering and, for a screen or partition inside the room, how much passes through it. <c>TransmissionLossDb</c>
/// is the sound reduction R in each octave band (null: none passes); the share τ = 10^(-R/10) goes through. Sound that goes through is not reflected, so a
/// face reflects 1 - max(α, τ): the material's absorption already counts what a thin panel lets through, and a panel that lets more through than its α says
/// reflects less. A wall of the room's boundary has nothing beyond it, so its transmission loss is not used.
/// </summary>
public sealed record SurfaceFinish(AbsorptionMaterial Material, OctaveSpectrum? Scattering = null, OctaveSpectrum? TransmissionLossDb = null)
{
    public OctaveSpectrum ScatteringCoefficients => Scattering ?? OctaveSpectrum.Flat(0);

    /// <summary>τ, the share of the energy that passes through, in one band (0 when none does).</summary>
    public double Transmission(int band) => TransmissionLossDb is { } r ? Math.Pow(10, -r[band] / 10) : 0;
}

/// <summary>A planar polygon face as entered by the user: corners in order around the face, either winding.</summary>
public sealed record PlanarFace(string Name, IReadOnlyList<Vec3> Vertices, SurfaceFinish Finish);

/// <summary>
/// A face of a built room. The corners are wound so that their normal points out of the room;
/// <see cref="InwardNormal"/> points into it. A free-standing screen (<see cref="IsScreen"/>) is inside the room with sound on both sides: its
/// <see cref="InwardNormal"/> is just its normal, either way, and both sides have its finish.
/// </summary>
public sealed record RoomFace(string Name, IReadOnlyList<Vec3> Vertices, SurfaceFinish Finish, Vec3 InwardNormal, double AreaM2, bool IsScreen = false);

/// <summary>
/// One free-standing screen: its faces (indices into <see cref="Room3D.Faces"/>), which share edges, and its free edges, the ones sound can bend round. An
/// edge lying on a wall, the floor or the ceiling (a screen standing on the floor, say) is not free: sound cannot pass that way.
/// </summary>
public sealed record ScreenPanel(IReadOnlyList<int> Faces, IReadOnlyList<(Vec3 A, Vec3 B)> Edges);

/// <summary>A triangle of a face's triangulation, tagged with the index of the face it belongs to.</summary>
internal readonly record struct FaceTriangle(Vec3 A, Vec3 B, Vec3 C, int Face);

/// <summary>
/// A closed room bounded by planar polygon faces, for three-dimensional energy ray tracing. The room does not
/// have to be convex: alcoves, L-shapes, stepped floors and balconies are fine, and faces may be non-convex
/// polygons (such as an L-shaped floor). Sound is blocked by the walls, so a receiver around a corner has no
/// direct sound.
/// </summary>
/// <remarks>
/// <para>
/// Construction validates the geometry: every face planar and a simple polygon, every edge shared by exactly two
/// faces (a closed surface, corners matching to a micrometre), all faces one connected surface, and a positive
/// enclosed volume. Faces may be entered with either winding; they are turned to face outward consistently.
/// </para>
/// <para>
/// Not supported yet: faces with holes (a wall with a window cut out must be split into polygons around it),
/// more than one separate closed surface (a free-standing column or furniture block inside the room), and
/// T-junctions, where one face's edge is split by a corner of another. The error message says how many edges
/// are unmatched, which is how a T-junction shows up.
/// </para>
/// <para>
/// Screens are thin free-standing surfaces inside the room (office screens, barriers, reflectors), given apart from the closed surface. Each is a planar
/// polygon reflecting from both sides; screens sharing edges form one <see cref="ScreenPanel"/>. They stop rays and block lines of sight, but are not part of
/// the room's volume or of the inside test.
/// </para>
/// </remarks>
public sealed class Room3D
{
    private const double Tolerance = 1e-6;
    private const double SegmentMarginM = 1e-6;

    // Directions for the inside test, irrational ratios so that a ray is unlikely to run along an edge of a mesh.
    private static readonly Vec3[] InsideTestDirections =
    {
        new Vec3(0.5773502691896258, 0.3090169943749474, 0.7547095802227720).Normalized(),
        new Vec3(-0.2679491924311227, 0.8660254037844386, -0.4226497308103742).Normalized(),
        new Vec3(0.1234567890123457, -0.5432109876543210, 0.8301234567890123).Normalized(),
    };

    private readonly TriangleBvh _bvh; // the closed surface
    private readonly TriangleBvh? _screenBvh;
    private readonly int[] _screenOf; // each face's screen panel, -1 for the closed surface
    private readonly int[] _faceFirstTriangle;
    private readonly bool[] _faceConvex;
    private readonly double[]? _planes; // a convex room's faces as plain numbers, six to a face

    /// <param name="faces">The closed surface.</param>
    /// <param name="screens">Free-standing screens inside it, each a planar polygon (see the remarks).</param>
    public Room3D(IEnumerable<PlanarFace> faces, IEnumerable<PlanarFace>? screens = null)
    {
        var input = faces?.ToList() ?? throw new ArgumentNullException(nameof(faces));
        if (input.Count < 4)
            throw new ArgumentException("A closed room needs at least four faces.", nameof(faces));

        var raw = input.Select(BuildRaw).ToList();
        var flip = Orient(raw);

        var allVertices = input.SelectMany(f => f.Vertices).ToList();
        var centroid = Average(allVertices);
        var signedVolume = 0.0;
        for (var i = 0; i < raw.Count; i++)
        {
            var outward = flip[i] ? -raw[i].Normal : raw[i].Normal;
            signedVolume += raw[i].AreaM2 * outward.Dot(raw[i].Face.Vertices[0] - centroid) / 3;
        }
        if (signedVolume < 0)
        {
            for (var i = 0; i < flip.Length; i++) flip[i] = !flip[i];
            signedVolume = -signedVolume;
        }
        if (signedVolume <= 1e-12)
            throw new ArgumentException("The faces do not enclose a positive volume.", nameof(faces));

        var built = new List<RoomFace>();
        for (var i = 0; i < raw.Count; i++)
        {
            var vertices = raw[i].Face.Vertices.ToList();
            if (flip[i]) vertices.Reverse();
            var outward = flip[i] ? -raw[i].Normal : raw[i].Normal;
            built.Add(new RoomFace(raw[i].Face.Name, vertices, raw[i].Face.Finish, -outward, raw[i].AreaM2));
        }

        BoundaryFaceCount = built.Count;
        VolumeM3 = signedVolume;
        IsConvex = IsConvexRoom(built, allVertices);
        if (IsConvex)
        {
            _planes = new double[built.Count * 6]; // each face: inward normal, then its first corner
            for (var i = 0; i < built.Count; i++)
            {
                var n = built[i].InwardNormal;
                var o = built[i].Vertices[0];
                (_planes[i * 6], _planes[i * 6 + 1], _planes[i * 6 + 2], _planes[i * 6 + 3], _planes[i * 6 + 4], _planes[i * 6 + 5]) = (n.X, n.Y, n.Z, o.X, o.Y, o.Z);
            }
        }

        // the screens follow the closed surface's faces, each with its own normal, both sides alike
        foreach (var screen in screens ?? Enumerable.Empty<PlanarFace>())
        {
            var raw1 = BuildRaw(screen);
            built.Add(new RoomFace(screen.Name, screen.Vertices.ToList(), screen.Finish, raw1.Normal, raw1.AreaM2, IsScreen: true));
        }
        Faces = built;

        _faceConvex = built.Select(f => IsConvexPolygon(f.Vertices, -f.InwardNormal)).ToArray();
        var triangles = new List<FaceTriangle>();
        _faceFirstTriangle = new int[built.Count + 1];
        for (var f = 0; f < built.Count; f++)
        {
            _faceFirstTriangle[f] = triangles.Count;
            foreach (var (a, b, c) in Triangulate(built[f], _faceConvex[f]))
                triangles.Add(new FaceTriangle(built[f].Vertices[a], built[f].Vertices[b], built[f].Vertices[c], f));
        }
        _faceFirstTriangle[built.Count] = triangles.Count;
        Triangles = triangles;
        var boundaryTriangles = _faceFirstTriangle[BoundaryFaceCount];
        _bvh = new TriangleBvh(triangles.Take(boundaryTriangles).ToList());
        _screenOf = Enumerable.Repeat(-1, built.Count).ToArray();
        if (built.Count > BoundaryFaceCount)
        {
            _screenBvh = new TriangleBvh(triangles.Skip(boundaryTriangles).ToList());
            for (var f = BoundaryFaceCount; f < built.Count; f++)
            {
                var centre = Average(built[f].Vertices.ToList());
                if (!Contains(centre))
                    throw new ArgumentException($"Screen '{built[f].Name}' is not inside the room.");
            }
            Screens = GroupScreens(built);
        }
        else Screens = Array.Empty<ScreenPanel>();
    }

    /// <summary>Every face: the closed surface's (<see cref="BoundaryFaceCount"/> of them) and then the screens'.</summary>
    public IReadOnlyList<RoomFace> Faces { get; }

    /// <summary>How many of <see cref="Faces"/> are the closed surface; the rest are screens.</summary>
    public int BoundaryFaceCount { get; }

    /// <summary>The free-standing screens, each a set of faces sharing edges.</summary>
    public IReadOnlyList<ScreenPanel> Screens { get; }

    public bool HasScreens => Screens.Count > 0;

    private double[]? _surfaceSizes;

    /// <summary>
    /// For each face, the characteristic size, metres, of the surface it is part of: 4 × area / perimeter of the faces that lie in one plane, share
    /// edges and have one finish (a wall drawn as many triangles is one surface; a window in it, of another finish, is another). That is the side
    /// of a square or the diameter of a disc. It sets how much a reflection off the surface is scattered by diffraction (see
    /// <see cref="EdgeScattering"/>).
    /// </summary>
    public IReadOnlyList<double> SurfaceSizesM => _surfaceSizes ??= SurfaceSizes();

    private double[] SurfaceSizes()
    {
        var count = Faces.Count;
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        (long, long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));
        bool OneSurface(int i, int j)
        {
            RoomFace a = Faces[i], b = Faces[j];
            return a.IsScreen == b.IsScreen && Equals(a.Finish, b.Finish) && Math.Abs(a.InwardNormal.Dot(b.InwardNormal)) > 1 - 1e-6
                   && Math.Abs(a.InwardNormal.Dot(b.Vertices[0] - a.Vertices[0])) < 1e-3;
        }

        var edges = new Dictionary<((long, long, long), (long, long, long)), (double Length, List<int> Faces)>();
        for (var i = 0; i < count; i++)
        {
            var v = Faces[i].Vertices;
            for (var k = 0; k < v.Count; k++)
            {
                Vec3 p = v[k], q = v[(k + 1) % v.Count];
                var (kp, kq) = (Key(p), Key(q));
                var key = kp.CompareTo(kq) <= 0 ? (kp, kq) : (kq, kp);
                if (!edges.TryGetValue(key, out var entry)) edges[key] = entry = ((q - p).Length, new List<int>());
                entry.Faces.Add(i);
            }
        }
        foreach (var (_, users) in edges.Values)
            for (var x = 0; x < users.Count; x++)
                for (var y = x + 1; y < users.Count; y++)
                    if (OneSurface(users[x], users[y])) parent[Find(users[x])] = Find(users[y]);

        var area = new double[count];
        var perimeter = new double[count];
        for (var i = 0; i < count; i++) area[Find(i)] += Faces[i].AreaM2;
        // an edge is on a surface's outline when only one of the surface's own faces uses it
        foreach (var (length, users) in edges.Values)
            foreach (var surface in users.GroupBy(Find).Where(g => g.Count() == 1))
                perimeter[surface.Key] += length;
        return Enumerable.Range(0, count).Select(i => perimeter[Find(i)] > 0 ? 4 * area[Find(i)] / perimeter[Find(i)] : 0).ToArray();
    }

    public double VolumeM3 { get; }

    /// <summary>The area of the closed surface (a screen's area is in its face).</summary>
    public double TotalAreaM2 => Faces.Where(f => !f.IsScreen).Sum(f => f.AreaM2);

    /// <summary>The same room and screens with each face's finish replaced (a third-octave pass, say); <paramref name="finish"/> is given each face's index and face.</summary>
    public Room3D WithFinishes(Func<int, RoomFace, SurfaceFinish> finish)
    {
        var faces = Faces.Select((f, i) => new PlanarFace(f.Name, f.Vertices, finish(i, f))).ToList();
        return new Room3D(faces.Take(BoundaryFaceCount), faces.Skip(BoundaryFaceCount));
    }

    /// <summary>
    /// The screens grouped into panels by their shared edges, each with its free edges: those used by one face of the panel and not lying on the closed
    /// surface (within 1 cm), the edges sound can bend round.
    /// </summary>
    private IReadOnlyList<ScreenPanel> GroupScreens(IReadOnlyList<RoomFace> faces)
    {
        var edges = new Dictionary<(Vec3, Vec3), List<int>>();
        for (var f = BoundaryFaceCount; f < faces.Count; f++)
        {
            var v = faces[f].Vertices;
            for (var i = 0; i < v.Count; i++)
            {
                var a = Snap(v[i]);
                var b = Snap(v[(i + 1) % v.Count]);
                var key = Less(a, b) ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var uses)) edges[key] = uses = new List<int>(2);
                uses.Add(f);
            }
        }
        // join faces that share an edge (union-find)
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Root(int f) { while (parent[f] != f) f = parent[f] = parent[parent[f]]; return f; }
        foreach (var uses in edges.Values)
            for (var k = 1; k < uses.Count; k++) parent[Root(uses[k])] = Root(uses[0]);

        var panels = new List<ScreenPanel>();
        foreach (var group in Enumerable.Range(BoundaryFaceCount, faces.Count - BoundaryFaceCount).GroupBy(Root))
        {
            var members = group.ToList();
            var free = edges.Where(kv => kv.Value.Count == 1 && members.Contains(kv.Value[0]))
                .Select(kv => kv.Key)
                .Where(e => BoundaryDistance((e.Item1 + e.Item2) * 0.5) > 0.01)
                .ToList();
            foreach (var f in members) _screenOf[f] = panels.Count;
            panels.Add(new ScreenPanel(members, free));
        }
        return panels;
    }

    /// <summary>The panels whose faces the straight line between two points passes through (not counting a face either end lies on).</summary>
    public IReadOnlyList<int> ScreensCrossed(Vec3 from, Vec3 to) => ScreenFacesCrossed(from, to).Select(c => c.Screen).ToList();

    /// <summary>The screens the straight line between two points passes through, each once, with the face of it the line goes through.</summary>
    public IReadOnlyList<(int Screen, int Face)> ScreenFacesCrossed(Vec3 from, Vec3 to)
    {
        if (!HasScreens) return Array.Empty<(int, int)>();
        var length = (to - from).Length;
        var found = new List<(int Screen, int Face)>();
        for (var f = BoundaryFaceCount; f < Faces.Count; f++)
        {
            var screen = _screenOf[f];
            if (found.Any(c => c.Screen == screen)) continue;
            var face = Faces[f];
            var d0 = face.InwardNormal.Dot(from - face.Vertices[0]);
            var d1 = face.InwardNormal.Dot(to - face.Vertices[0]);
            if (d0 * d1 >= 0) continue;
            var t = d0 / (d0 - d1);
            if (t * length <= SegmentMarginM || (1 - t) * length <= SegmentMarginM) continue;
            if (FaceContains(f, from + (to - from) * t)) found.Add((screen, f));
        }
        return found;
    }

    /// <summary>True if the room is convex: every corner on the inner side of every face. Convex rooms take faster paths.</summary>
    public bool IsConvex { get; }

    internal IReadOnlyList<FaceTriangle> Triangles { get; }

    /// <summary>True if the point lies strictly inside the room.</summary>
    public bool Contains(Vec3 point)
    {
        if (IsConvex)
        {
            // the same arithmetic as InwardNormal.Dot(point - Vertices[0]), over plain arrays: a model of tens of thousands of faces is asked this for every grid point
            var p = _planes!;
            for (var i = 0; i < p.Length; i += 6)
                if (!(p[i] * (point.X - p[i + 3]) + p[i + 1] * (point.Y - p[i + 4]) + p[i + 2] * (point.Z - p[i + 5]) > 0)) return false;
            return true;
        }

        // A ray from an inside point crosses the surface an odd number of times. Three directions vote, so a ray that
        // happens to run along an edge cannot decide it alone.
        var inside = 0;
        foreach (var direction in InsideTestDirections)
            if (_bvh.CountCrossings(point, direction) % 2 == 1) inside++;
        return inside >= 2;
    }

    /// <summary>
    /// Distance from a point to the nearest surface, a screen included. Negative outside the room. For a convex room the closed surface's part is the smallest
    /// distance to a face plane; otherwise the exact distance to the nearest triangle.
    /// </summary>
    public double DistanceToBoundary(Vec3 point)
    {
        var wall = BoundaryDistance(point);
        if (_screenBvh is null || wall < 0) return wall;
        return Math.Min(wall, _screenBvh.NearestDistance(point, DistanceToTriangle));
    }

    private double BoundaryDistance(Vec3 point)
    {
        if (IsConvex)
        {
            var p = _planes!;
            var least = double.PositiveInfinity;
            for (var i = 0; i < p.Length; i += 6)
                least = Math.Min(least, p[i] * (point.X - p[i + 3]) + p[i + 1] * (point.Y - p[i + 4]) + p[i + 2] * (point.Z - p[i + 5]));
            return least;
        }

        var nearest = _bvh.NearestDistance(point, DistanceToTriangle);
        return Contains(point) ? nearest : -nearest;
    }

    /// <summary>True if the straight line between two points is not blocked by any wall or screen.</summary>
    public bool IsClear(Vec3 from, Vec3 to) => IsClearOfWalls(from, to) && (_screenBvh is null || !_screenBvh.IsBlocked(from, to, SegmentMarginM));

    /// <summary>True if the straight line between two points is not blocked by the closed surface (screens may be in the way).</summary>
    public bool IsClearOfWalls(Vec3 from, Vec3 to) => IsConvex || !_bvh.IsBlocked(from, to, SegmentMarginM);

    /// <summary>Nearest intersection of a ray with the room's faces and screens, ignoring the face it just left.</summary>
    internal (double Length, int Face) NearestHit(Vec3 origin, Vec3 direction, int skipFace)
    {
        var hit = _bvh.Nearest(origin, direction, skipFace);
        if (_screenBvh is null) return hit;
        var screen = _screenBvh.Nearest(origin, direction, skipFace);
        return screen.Face >= 0 && screen.Length < hit.Length ? screen : hit;
    }

    /// <summary>True if the point lies on the given face (within its polygon, in its plane's tolerance).</summary>
    /// <summary>
    /// Whether a path that turns at <paramref name="point"/> (a reflection off a wall) goes from one side of a screen to the other there: the point lies on a
    /// screen, its edge where it meets the wall say, and the legs to <paramref name="before"/> and <paramref name="after"/> leave it on opposite sides. A
    /// screen meets the wall it stands against, so such a path is blocked; the legs alone do not show it, as each only touches the screen at its end.
    /// </summary>
    internal bool PassesThroughScreenAt(Vec3 point, Vec3 before, Vec3 after)
    {
        for (var f = BoundaryFaceCount; f < Faces.Count; f++)
        {
            var n = Faces[f].InwardNormal;
            if (Math.Abs(n.Dot(point - Faces[f].Vertices[0])) > SegmentMarginM) continue;
            double b = n.Dot(before - point), a = n.Dot(after - point);
            if (b * a >= 0 || Math.Abs(b) <= SegmentMarginM || Math.Abs(a) <= SegmentMarginM) continue;
            if (FaceContains(f, point)) return true;
        }
        return false;
    }

    internal bool FaceContains(int face, Vec3 point)
    {
        var room = Faces[face];
        if (_faceConvex[face])
            return InsideConvexPolygon(room.Vertices, room.InwardNormal, point);

        for (var t = _faceFirstTriangle[face]; t < _faceFirstTriangle[face + 1]; t++)
        {
            var triangle = Triangles[t];
            if (InsideConvexPolygon(new[] { triangle.A, triangle.B, triangle.C }, room.InwardNormal, point))
                return true;
        }
        return false;
    }

    /// <summary>A rectangular box, every face with the same finish.</summary>
    public static Room3D Shoebox(double lengthM, double widthM, double heightM, SurfaceFinish finish) =>
        Extrude(new[] { (0.0, 0.0), (lengthM, 0.0), (lengthM, widthM), (0.0, widthM) }, heightM, finish, finish, finish);

    /// <summary>
    /// A footprint extruded vertically from the floor (z = 0) to the ceiling (z = <paramref name="heightM"/>), e.g. a
    /// room outline imported from a DXF file. The footprint may be non-convex (an L-shaped room, say) as long as it
    /// does not cross itself.
    /// </summary>
    public static Room3D Extrude(IReadOnlyList<(double X, double Y)> footprint, double heightM,
        SurfaceFinish floor, SurfaceFinish walls, SurfaceFinish ceiling)
    {
        if (footprint.Count < 3) throw new ArgumentException("A footprint needs at least three corners.", nameof(footprint));
        if (heightM <= 0) throw new ArgumentOutOfRangeException(nameof(heightM), "The height must be positive.");

        var bottom = footprint.Select(p => new Vec3(p.X, p.Y, 0)).ToList();
        var top = footprint.Select(p => new Vec3(p.X, p.Y, heightM)).ToList();
        var faces = new List<PlanarFace>
        {
            new("Floor", bottom, floor),
            new("Ceiling", top, ceiling),
        };
        for (var i = 0; i < footprint.Count; i++)
        {
            var j = (i + 1) % footprint.Count;
            faces.Add(new PlanarFace($"Wall {i + 1}", new[] { bottom[i], bottom[j], top[j], top[i] }, walls));
        }
        return new Room3D(faces);
    }

    private sealed record RawFace(PlanarFace Face, Vec3 Normal, double AreaM2);

    private static RawFace BuildRaw(PlanarFace face)
    {
        var vertices = face.Vertices.ToList();
        if (vertices.Count < 3)
            throw new ArgumentException($"Face '{face.Name}' needs at least three corners.");

        // Newell's method: the sum of consecutive cross products is twice the area times the normal.
        var doubleArea = Vec3.Zero;
        for (var i = 0; i < vertices.Count; i++)
            doubleArea += vertices[i].Cross(vertices[(i + 1) % vertices.Count]);
        var areaM2 = doubleArea.Length / 2;
        if (areaM2 < 1e-9)
            throw new ArgumentException($"Face '{face.Name}' is degenerate (zero area).");

        var normal = doubleArea.Normalized();
        var origin = vertices[0];
        foreach (var v in vertices)
            if (Math.Abs(normal.Dot(v - origin)) > Tolerance)
                throw new ArgumentException($"Face '{face.Name}' is not planar.");

        for (var i = 0; i < vertices.Count; i++)
            if (Snap(vertices[i]) == Snap(vertices[(i + 1) % vertices.Count]))
                throw new ArgumentException($"Face '{face.Name}' repeats a corner.");

        ValidateFinish(face);
        return new RawFace(face, normal, areaM2);
    }

    private static void ValidateFinish(PlanarFace face)
    {
        var alpha = face.Finish.Material.ToOctaveSpectrum();
        var scatter = face.Finish.ScatteringCoefficients;
        for (var b = 0; b < 8; b++)
        {
            if (alpha[b] < 0 || alpha[b] > 1)
                throw new ArgumentException($"Face '{face.Name}' has an absorption coefficient outside 0-1 in the {OctaveSpectrum.Frequencies[b]} Hz band.");
            if (scatter[b] < 0 || scatter[b] > 1)
                throw new ArgumentException($"Face '{face.Name}' has a scattering coefficient outside 0-1 in the {OctaveSpectrum.Frequencies[b]} Hz band.");
        }
    }

    /// <summary>
    /// Checks the surface is closed and connected, and returns, for each face, whether its corners must be reversed so
    /// that every shared edge is walked in opposite directions by its two faces (a consistent orientation). Whether
    /// that orientation faces outward or inward is settled afterwards, from the sign of the enclosed volume.
    /// </summary>
    private static bool[] Orient(IReadOnlyList<RawFace> faces)
    {
        // Each undirected edge: the faces using it, and whether each walks it from the smaller corner to the larger.
        var edges = new Dictionary<(Vec3, Vec3), List<(int Face, bool Forward)>>();
        for (var f = 0; f < faces.Count; f++)
        {
            var v = faces[f].Face.Vertices;
            for (var i = 0; i < v.Count; i++)
            {
                var a = Snap(v[i]);
                var b = Snap(v[(i + 1) % v.Count]);
                var forward = Less(a, b);
                var key = forward ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var uses)) edges[key] = uses = new List<(int, bool)>();
                uses.Add((f, forward));
            }
        }

        var open = edges.Values.Count(u => u.Count < 2);
        var shared = edges.Values.Count(u => u.Count > 2);
        if (open > 0 || shared > 0)
            throw new ArgumentException(
                $"The faces do not form a closed surface: every edge must be shared by exactly two faces ({open} edges have only one face, {shared} have more than two). " +
                "A corner of one face lying along the edge of another (a T-junction) shows up here.");

        var neighbours = new List<(int Other, bool MyForward, bool OtherForward)>[faces.Count];
        for (var f = 0; f < faces.Count; f++) neighbours[f] = new();
        foreach (var uses in edges.Values)
        {
            neighbours[uses[0].Face].Add((uses[1].Face, uses[0].Forward, uses[1].Forward));
            neighbours[uses[1].Face].Add((uses[0].Face, uses[1].Forward, uses[0].Forward));
        }

        var flip = new bool[faces.Count];
        var visited = new bool[faces.Count];
        var queue = new Queue<int>();
        visited[0] = true;
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            var f = queue.Dequeue();
            foreach (var (other, myForward, otherForward) in neighbours[f])
            {
                // The two faces must walk the shared edge in opposite directions once flips are applied.
                var wanted = (!(myForward ^ flip[f])) ^ otherForward;
                if (!visited[other])
                {
                    visited[other] = true;
                    flip[other] = wanted;
                    queue.Enqueue(other);
                }
                else if (flip[other] != wanted)
                {
                    throw new ArgumentException("The faces cannot be given a consistent orientation (the surface is twisted, like a Möbius strip, or faces overlap).");
                }
            }
        }
        if (visited.Any(v => !v))
            throw new ArgumentException("The faces form more than one separate surface. A room must be one connected closed surface (free-standing solids inside it are not supported yet).");
        return flip;
    }

    // Testing every corner against every face is faces x corners, which a model of tens of thousands of triangles cannot afford (a 80 000-triangle dome took
    // 19 s). Above this many tests the check is made edge by edge instead: a closed, consistently oriented, connected surface whose every edge is convex is a
    // convex body, and an edge that is concave is just as certainly a corner on the wrong side of a face, so a non-convex room is still found.
    private const long FullConvexTestLimit = 20_000_000;

    private static bool IsConvexRoom(IReadOnlyList<RoomFace> faces, IReadOnlyList<Vec3> allVertices)
    {
        if ((long)faces.Count * allVertices.Count > FullConvexTestLimit) return EveryEdgeConvex(faces);
        foreach (var face in faces)
        {
            var origin = face.Vertices[0];
            if (allVertices.Any(v => face.InwardNormal.Dot(v - origin) < -Tolerance)) return false;
        }
        return true;
    }

    /// <summary>For each edge, the corners of the face on its other side must not lie outside this face's plane.</summary>
    private static bool EveryEdgeConvex(IReadOnlyList<RoomFace> faces)
    {
        var onEdge = new Dictionary<(Vec3, Vec3), List<int>>();
        for (var f = 0; f < faces.Count; f++)
        {
            var v = faces[f].Vertices;
            for (var i = 0; i < v.Count; i++)
            {
                var a = Snap(v[i]);
                var b = Snap(v[(i + 1) % v.Count]);
                var key = Less(a, b) ? (a, b) : (b, a);
                if (!onEdge.TryGetValue(key, out var uses)) onEdge[key] = uses = new List<int>(2);
                uses.Add(f);
            }
        }
        foreach (var ((a, b), uses) in onEdge)
            foreach (var f in uses)
                foreach (var g in uses)
                {
                    if (g == f) continue;
                    var origin = faces[f].Vertices[0];
                    foreach (var corner in faces[g].Vertices)
                    {
                        var s = Snap(corner);
                        if (s == a || s == b) continue;
                        if (faces[f].InwardNormal.Dot(corner - origin) < -Tolerance) return false;
                    }
                }
        return true;
    }

    private static bool IsConvexPolygon(IReadOnlyList<Vec3> vertices, Vec3 normal)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            var edge = vertices[(i + 1) % vertices.Count] - vertices[i];
            var next = vertices[(i + 2) % vertices.Count] - vertices[(i + 1) % vertices.Count];
            if (edge.Cross(next).Dot(normal) < -Tolerance) return false;
        }
        return true;
    }

    /// <summary>
    /// A convex polygon contains a point exactly when the point is on the same side of every edge: the sign of
    /// (edge × offset) · normal must not change.
    /// </summary>
    private static bool InsideConvexPolygon(IReadOnlyList<Vec3> v, Vec3 normal, Vec3 point)
    {
        var positive = false;
        var negative = false;
        for (var i = 0; i < v.Count; i++)
        {
            var edge = v[(i + 1) % v.Count] - v[i];
            var side = edge.Cross(point - v[i]).Dot(normal);
            if (side > Tolerance) positive = true;
            else if (side < -Tolerance) negative = true;
            if (positive && negative) return false;
        }
        return true;
    }

    /// <summary>
    /// Triangles (as corner indices) covering a face. A convex face is fanned from its first corner; a non-convex face
    /// is ear-clipped in the plane of the face.
    /// </summary>
    private static List<(int, int, int)> Triangulate(RoomFace face, bool convex)
    {
        var v = face.Vertices;
        var triangles = new List<(int, int, int)>();
        if (convex)
        {
            for (var k = 1; k < v.Count - 1; k++) triangles.Add((0, k, k + 1));
            return triangles;
        }

        // Project onto the two axes the face is least tilted away from, so the projection keeps its shape.
        var normal = -face.InwardNormal;
        var ax = Math.Abs(normal.X);
        var ay = Math.Abs(normal.Y);
        var az = Math.Abs(normal.Z);
        var drop = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        (double U, double V) Project(Vec3 p) => drop switch
        {
            0 => (p.Y, p.Z),
            1 => (p.Z, p.X),
            _ => (p.X, p.Y),
        };
        var points = v.Select(Project).ToArray();

        // Work counter-clockwise in the projection.
        var signedArea = 0.0;
        for (var i = 0; i < points.Length; i++)
        {
            var (u1, v1) = points[i];
            var (u2, v2) = points[(i + 1) % points.Length];
            signedArea += u1 * v2 - u2 * v1;
        }
        var remaining = Enumerable.Range(0, v.Count).ToList();
        if (signedArea < 0) remaining.Reverse();

        double Cross(int a, int b, int c) =>
            (points[b].U - points[a].U) * (points[c].V - points[a].V) - (points[b].V - points[a].V) * (points[c].U - points[a].U);

        bool InsideTriangle(int p, int a, int b, int c) =>
            Cross(a, b, p) >= -1e-12 && Cross(b, c, p) >= -1e-12 && Cross(c, a, p) >= -1e-12;

        var scale = Math.Max(1e-12, face.AreaM2 * 1e-9);
        while (remaining.Count > 3)
        {
            var clipped = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var a = remaining[(i + remaining.Count - 1) % remaining.Count];
                var b = remaining[i];
                var c = remaining[(i + 1) % remaining.Count];
                var cross = Cross(a, b, c);

                if (Math.Abs(cross) <= scale)
                {
                    // A corner in a straight line with its neighbours adds no area: drop it.
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (cross < 0) continue; // a reflex corner cannot be an ear
                if (remaining.Any(p => p != a && p != b && p != c && InsideTriangle(p, a, b, c))) continue;

                triangles.Add(Wind(v, normal, a, b, c));
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
                throw new ArgumentException($"Face '{face.Name}' is not a simple polygon (its edges cross or it has a hole).");
        }
        triangles.Add(Wind(v, normal, remaining[0], remaining[1], remaining[2]));
        return triangles;
    }

    private static (int, int, int) Wind(IReadOnlyList<Vec3> v, Vec3 normal, int a, int b, int c) =>
        (v[b] - v[a]).Cross(v[c] - v[a]).Dot(normal) >= 0 ? (a, b, c) : (a, c, b);

    /// <summary>Distance from a point to a triangle: to the nearest point on it (Ericson, Real-Time Collision Detection).</summary>
    private static double DistanceToTriangle(Vec3 p, FaceTriangle t)
    {
        var a = t.A;
        var b = t.B;
        var c = t.C;
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = ab.Dot(ap);
        var d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0) return (p - a).Length;

        var bp = p - b;
        var d3 = ab.Dot(bp);
        var d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3) return (p - b).Length;

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return (p - (a + ab * (d1 / (d1 - d3)))).Length;

        var cp = p - c;
        var d5 = ab.Dot(cp);
        var d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6) return (p - c).Length;

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return (p - (a + ac * (d2 / (d2 - d6)))).Length;

        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).Length;

        var denominator = 1 / (va + vb + vc);
        var closest = a + ab * (vb * denominator) + ac * (vc * denominator);
        return (p - closest).Length;
    }

    private static Vec3 Average(IReadOnlyList<Vec3> points)
    {
        var sum = Vec3.Zero;
        foreach (var p in points) sum += p;
        return sum * (1.0 / points.Count);
    }

    // Rounding to a micrometre makes corners that coincide up to floating-point noise compare equal;
    // adding 0.0 turns any -0.0 into 0.0 so the dictionary keys hash consistently.
    private static Vec3 Snap(Vec3 p) =>
        new(Math.Round(p.X * 1e6) / 1e6 + 0.0, Math.Round(p.Y * 1e6) / 1e6 + 0.0, Math.Round(p.Z * 1e6) / 1e6 + 0.0);

    private static bool Less(Vec3 a, Vec3 b) =>
        a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
}
