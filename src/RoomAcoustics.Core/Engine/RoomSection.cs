namespace RoomAcoustics.Core;

/// <summary>Which vertical plane a section is taken on: where <c>x</c> is constant, or where <c>y</c> is.</summary>
public enum SectionAxis
{
    /// <summary>The plane x = position. The section is drawn with y across and z up.</summary>
    X,

    /// <summary>The plane y = position. The section is drawn with x across and z up.</summary>
    Y,
}

/// <summary>
/// A vertical plane through the room along a line in the plan, from (X1, Y1) to (X2, Y2) in metres. The section is drawn with the distance
/// along the line from its start across and the height up, and covers only the length of the line.
/// </summary>
public readonly record struct SectionLine(double X1, double Y1, double X2, double Y2)
{
    /// <summary>The length of the line in the plan.</summary>
    public double LengthM => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));

    /// <summary>The unit vector along the line (horizontal). Zero for a line of no length.</summary>
    public Vec3 Direction => LengthM < 1e-9 ? new Vec3(0, 0, 0) : new Vec3((X2 - X1) / LengthM, (Y2 - Y1) / LengthM, 0);

    /// <summary>The horizontal unit vector square to the line, to its left.</summary>
    public Vec3 Normal => new(-Direction.Y, Direction.X, 0);

    /// <summary>How far along the line a point is, in metres from its start (the point's height is ignored).</summary>
    public double Across(Vec3 p) => (p.X - X1) * Direction.X + (p.Y - Y1) * Direction.Y;

    /// <summary>How far a point is to the left of the plane, in metres (negative on the right).</summary>
    public double OffPlane(Vec3 p) => (p.X - X1) * Normal.X + (p.Y - Y1) * Normal.Y;

    internal void Validate()
    {
        if (!double.IsFinite(X1 + Y1 + X2 + Y2)) throw new ArgumentException("The section line's ends must be finite numbers.");
        if (LengthM < 1e-6) throw new ArgumentException("The section line's two ends are the same point: give it some length.");
    }
}

/// <summary>A straight piece of a section: from (U1, V1) to (U2, V2), where U is across the section and V is height.</summary>
public readonly record struct SectionSegment(double U1, double V1, double U2, double V2);

/// <summary>A vertical slice through a room: a grid of receivers on a vertical plane, and the room's outline on it.</summary>
public static class RoomSection
{
    /// <summary>The coordinate of a point across the section (y for a plane of constant x, x for one of constant y).</summary>
    public static double Across(SectionAxis axis, Vec3 p) => axis == SectionAxis.X ? p.Y : p.X;

    /// <summary>
    /// Points on a vertical plane, one per cell of a square grid of side <paramref name="spacingM"/> (cell centres, from half
    /// a cell in from the room's bounding box). A point is kept only if it is at least <paramref name="clearanceM"/> from every
    /// surface, so each receiver sphere fits inside the room.
    /// </summary>
    public static IReadOnlyList<Vec3> Grid(Room3D room, SectionAxis axis, double positionM, double spacingM, double clearanceM)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (spacingM <= 0) throw new ArgumentOutOfRangeException(nameof(spacingM), "The grid spacing must be positive.");
        if (clearanceM < 0) throw new ArgumentOutOfRangeException(nameof(clearanceM), "The clearance cannot be negative.");

        var corners = room.Faces.SelectMany(f => f.Vertices).ToList();
        var minAcross = corners.Min(v => Across(axis, v));
        var maxAcross = corners.Max(v => Across(axis, v));
        var minZ = corners.Min(v => v.Z);
        var maxZ = corners.Max(v => v.Z);

        var points = new List<Vec3>();
        for (var u = minAcross + spacingM / 2; u <= maxAcross; u += spacingM)
            for (var z = minZ + spacingM / 2; z <= maxZ; z += spacingM)
            {
                var point = axis == SectionAxis.X ? new Vec3(positionM, u, z) : new Vec3(u, positionM, z);
                if (room.DistanceToBoundary(point) >= clearanceM)
                    points.Add(point);
            }

        if (points.Count == 0)
            throw new ArgumentException("No grid point is clear of the surfaces on that plane: move the plane into the room, or reduce the spacing or the clearance.");
        return points;
    }

    /// <summary>
    /// Points on the vertical plane through a line in the plan, one per cell of a square grid of side <paramref name="spacingM"/>: cell centres
    /// from half a cell along the line from its start (and up from half a cell above the room's lowest point), none beyond the line's end. A
    /// point is kept only if it is at least <paramref name="clearanceM"/> from every surface.
    /// </summary>
    public static IReadOnlyList<Vec3> Grid(Room3D room, SectionLine line, double spacingM, double clearanceM)
    {
        ArgumentNullException.ThrowIfNull(room);
        line.Validate();
        if (spacingM <= 0) throw new ArgumentOutOfRangeException(nameof(spacingM), "The grid spacing must be positive.");
        if (clearanceM < 0) throw new ArgumentOutOfRangeException(nameof(clearanceM), "The clearance cannot be negative.");

        var corners = room.Faces.SelectMany(f => f.Vertices).ToList();
        var minZ = corners.Min(v => v.Z);
        var maxZ = corners.Max(v => v.Z);
        var direction = line.Direction;

        var points = new List<Vec3>();
        for (var u = spacingM / 2; u <= line.LengthM; u += spacingM)
            for (var z = minZ + spacingM / 2; z <= maxZ; z += spacingM)
            {
                var point = new Vec3(line.X1 + direction.X * u, line.Y1 + direction.Y * u, z);
                if (room.DistanceToBoundary(point) >= clearanceM)
                    points.Add(point);
            }

        if (points.Count == 0)
            throw new ArgumentException("No grid point is clear of the surfaces on that plane: move the line into the room, or reduce the spacing or the clearance.");
        return points;
    }

    /// <summary>
    /// The room's outline where the plane cuts it: each face that the plane crosses gives the pieces of the face on the plane.
    /// A face that lies in the plane gives nothing. Works for non-convex faces too, by taking the crossings along the cut in pairs.
    /// </summary>
    public static IReadOnlyList<SectionSegment> Outline(Room3D room, SectionAxis axis, double positionM)
    {
        ArgumentNullException.ThrowIfNull(room);
        return Cut(room, axis == SectionAxis.X ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0), positionM, p => Across(axis, p));
    }

    /// <summary>
    /// The room's outline on the vertical plane through a line in the plan, with the distance along the line across. The whole plane is cut,
    /// beyond the ends of the line as well (a plot of the line's length shows only the part inside it).
    /// </summary>
    public static IReadOnlyList<SectionSegment> Outline(Room3D room, SectionLine line)
    {
        ArgumentNullException.ThrowIfNull(room);
        line.Validate();
        var normal = line.Normal;
        return Cut(room, normal, line.X1 * normal.X + line.Y1 * normal.Y, line.Across);
    }

    private static List<SectionSegment> Cut(Room3D room, Vec3 normal, double positionM, Func<Vec3, double> across)
    {
        var segments = new List<SectionSegment>();

        foreach (var face in room.Faces)
        {
            var v = face.Vertices;
            var signed = v.Select(p => p.Dot(normal) - positionM).ToArray();
            if (signed.All(d => Math.Abs(d) < 1e-9)) continue; // the face lies in the plane

            var cut = face.InwardNormal.Cross(normal);
            if (cut.Length < 1e-9) continue; // parallel to the plane, and not in it
            cut = cut.Normalized();

            var crossings = new List<(double Along, Vec3 Point)>();
            for (var i = 0; i < v.Count; i++)
            {
                var j = (i + 1) % v.Count;
                if ((signed[i] < 0) == (signed[j] < 0)) continue;
                var t = signed[i] / (signed[i] - signed[j]);
                var point = v[i] + (v[j] - v[i]) * t;
                crossings.Add((point.Dot(cut), point));
            }

            crossings.Sort((a, b) => a.Along.CompareTo(b.Along));
            for (var k = 0; k + 1 < crossings.Count; k += 2)
            {
                var a = crossings[k].Point;
                var b = crossings[k + 1].Point;
                segments.Add(new SectionSegment(across(a), a.Z, across(b), b.Z));
            }
        }
        return segments;
    }
}

/// <summary>Summary figures for a metric over a set of receivers: the spread across a room, as a designer would quote it.</summary>
/// <param name="Count">How many finite values there were.</param>
/// <param name="Mean">The arithmetic mean (of dB values too, which is the usual way to quote a level spread).</param>
/// <param name="StdDev">The standard deviation of the values about the mean (the population figure: the receivers are the whole set).</param>
/// <param name="Min">The smallest value.</param>
/// <param name="Max">The largest value.</param>
/// <param name="Median">The middle value.</param>
public sealed record MapStatistics(int Count, double Mean, double StdDev, double Min, double Max, double Median)
{
    /// <summary>Statistics of the finite values (any NaN or infinity is left out); null if there are none.</summary>
    public static MapStatistics? Of(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return null;

        var mean = sorted.Average();
        var variance = sorted.Sum(x => (x - mean) * (x - mean)) / sorted.Length;
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return new MapStatistics(sorted.Length, mean, Math.Sqrt(variance), sorted[0], sorted[^1], median);
    }

    /// <summary>The range, largest less smallest.</summary>
    public double Spread => Max - Min;

    /// <summary>The fraction (0 to 1) of the finite values within <paramref name="tolerance"/> either side of <paramref name="centre"/>.</summary>
    public static double FractionWithin(IEnumerable<double> values, double centre, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance cannot be negative.");
        var finite = values.Where(double.IsFinite).ToArray();
        return finite.Length == 0 ? 0 : finite.Count(x => Math.Abs(x - centre) <= tolerance + 1e-12) / (double)finite.Length;
    }
}
