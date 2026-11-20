namespace RoomAcoustics.Core;

/// <summary>
/// A specular reflection path: its length from source to receiver, the faces it
/// reflects from, in order (the first entry is the first wall hit), and the point
/// of that first hit, which gives the direction the sound leaves the source. <see cref="FinalDirection"/> is the unit
/// vector of the last leg, the direction the sound is travelling when it reaches the receiver. <see cref="Path"/> is the
/// whole route, source then each reflection point then receiver, and is only filled in when asked for. <see cref="ScreenedLegs"/> are the legs a screen
/// stands in the way of, which reach the next point only by bending round it (see <see cref="ScreenDiffraction"/>); null when none is.
/// <see cref="Bend"/> is set for a path that bends round a corner of the room (see <see cref="ImageSourceEnumerator.EnumerateRoundCorners"/>): its
/// <see cref="Path"/> then runs from the source to the turn when the reflections come first, or from the turn to the receiver when they come after it, and
/// <see cref="FirstReflection"/> is the turn when the bend comes first.
/// </summary>
public sealed record ImageArrival(double DistanceM, IReadOnlyList<int> Faces, Vec3 FirstReflection, Vec3 FinalDirection,
    IReadOnlyList<Vec3>? Path = null, IReadOnlyList<(Vec3 From, Vec3 To)>? ScreenedLegs = null, CornerDiffraction.Path? Bend = null);

/// <summary>
/// Image-source method for specular reflection paths in a room. Each face
/// sequence reflects the source across its planes to an image point, and
/// the sequence is a real path only if the straight line from the image back
/// to the receiver crosses each face, in order, inside its polygon. Those paths
/// are found exactly, unlike the sampled paths of the ray tracer.
/// </summary>
/// <remarks>
/// <para>
/// The number of face sequences grows as (F-1)^order, for F faces, so the
/// order is capped (up to 10). Paths of higher order are not returned.
/// <see cref="EnergyRayTracer"/> uses this with its own ray-traced paths of
/// higher order, so no path is missing or counted twice.
/// </para>
/// <para>
/// A face is only tried when the image being reflected is in front of it, which discards the sequences that
/// cannot be paths. In a non-convex room each leg of a path must also be clear of the walls, and must arrive at
/// each face from inside the room.
/// </para>
/// <para>
/// A screen reflects from either side, so it is tried whichever side the image is on, and a path off it must arrive and leave on the same side. A leg a
/// screen stands in the way of is kept, and marked, for the screen's diffraction to cut.
/// </para>
/// </remarks>
public static class ImageSourceEnumerator
{
    public const int MaxSupportedOrder = 10;

    private const double Tolerance = 1e-9;

    /// <summary>
    /// All valid specular paths of up to <paramref name="maxOrder"/> reflections, unordered by arrival time.
    /// <paramref name="withPaths"/> keeps each route's reflection points in <see cref="ImageArrival.Path"/>; it is off for
    /// the ray tracer, which has no use for them and holds the arrivals of every receiver at once.
    /// </summary>
    public static IReadOnlyList<ImageArrival> Enumerate(Room3D room, Vec3 source, Vec3 receiver, int maxOrder, bool withPaths = false)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (maxOrder < 1 || maxOrder > MaxSupportedOrder)
            throw new ArgumentOutOfRangeException(nameof(maxOrder), $"The image-source order must be between 1 and {MaxSupportedOrder}.");
        if (!room.Contains(source)) throw new ArgumentException("The source must be inside the room.", nameof(source));
        if (!room.Contains(receiver)) throw new ArgumentException("The receiver must be inside the room.", nameof(receiver));

        var arrivals = new List<ImageArrival>();
        var seen = new HashSet<(long, long, long)>();
        Search(room, source, receiver, source, -1, new List<int>(), new List<Vec3>(), maxOrder, withPaths, arrivals, seen);
        return arrivals;
    }

    private static void Search(Room3D room, Vec3 source, Vec3 receiver, Vec3 image, int lastFace, List<int> faces, List<Vec3> images,
        int maxOrder, bool withPaths, List<ImageArrival> arrivals, HashSet<(long, long, long)> seen)
    {
        for (var f = 0; f < room.Faces.Count; f++)
        {
            if (f == lastFace) continue;

            // The image being reflected must be in front of the face. Behind it, no path can bounce off the face.
            var face = room.Faces[f];
            var side = face.InwardNormal.Dot(image - face.Vertices[0]);
            if (face.IsScreen ? Math.Abs(side) <= Tolerance : side <= Tolerance) continue;

            var reflected = Reflect(image, face);
            faces.Add(f);
            images.Add(reflected);

            // A path through an edge is valid for both orders of the two faces it touches, with the same image point.
            // An image point fixes the straight line, so each geometric path is counted once: keep the first.
            if (IsVisible(room, source, receiver, faces, images, out var first, out var route, out var screened) && seen.Add(Key(reflected)))
                arrivals.Add(new ImageArrival((receiver - reflected).Length, faces.ToArray(), first, (receiver - reflected).Normalized(),
                    withPaths ? Enumerable.Reverse(route).ToArray() : null, screened));

            // Every sequence is extended, not just the valid ones: a sequence can be invalid while a longer one is valid.
            if (faces.Count < maxOrder)
                Search(room, source, receiver, reflected, f, faces, images, maxOrder, withPaths, arrivals, seen);

            faces.RemoveAt(faces.Count - 1);
            images.RemoveAt(images.Count - 1);
        }
    }

    /// <summary>
    /// The specular paths of 1 to <paramref name="maxOrder"/> reflections that a corner of the room hides from the receiver, bent round the corner instead: the
    /// reflections all come before the bend (images of the source) or all after it (images of the receiver). A path is kept only where the corner shadows the
    /// receiver from the image, so a reflection in sight is not counted again, and it must be clear of the walls either side of the turn. The bend is a single
    /// diffraction, cut by <see cref="CornerDiffraction.Factor"/> for its <see cref="ImageArrival.Bend"/>.
    /// </summary>
    public static IReadOnlyList<ImageArrival> EnumerateRoundCorners(Room3D room, Vec3 source, Vec3 receiver, int maxOrder, CornerDiffraction corners, bool withPaths = false)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(corners);
        if (maxOrder < 1 || maxOrder > MaxSupportedOrder)
            throw new ArgumentOutOfRangeException(nameof(maxOrder), $"The image-source order must be between 1 and {MaxSupportedOrder}.");
        var arrivals = new List<ImageArrival>();
        if (corners.Edges.Count == 0) return arrivals;
        var seen = new HashSet<((long, long, long), int, bool)>();
        // reflections then the bend: images of the source; the bend then reflections: images of the receiver, the same kind of path walked backwards
        SearchBent(room, source, receiver, source, -1, new List<int>(), new List<Vec3>(), maxOrder, corners, false, withPaths, arrivals, seen);
        SearchBent(room, receiver, source, receiver, -1, new List<int>(), new List<Vec3>(), maxOrder, corners, true, withPaths, arrivals, seen);
        return arrivals;
    }

    /// <summary>
    /// The face sequences from <paramref name="start"/> as in <see cref="Search"/>; each one's image is tried round every corner edge on its way to
    /// <paramref name="end"/>. <paramref name="backwards"/> says <paramref name="start"/> is the receiver, so the arrival is turned round into source-to-receiver order.
    /// </summary>
    private static void SearchBent(Room3D room, Vec3 start, Vec3 end, Vec3 image, int lastFace, List<int> faces, List<Vec3> images, int maxOrder,
        CornerDiffraction corners, bool backwards, bool withPaths, List<ImageArrival> arrivals, HashSet<((long, long, long), int, bool)> seen)
    {
        for (var f = 0; f < room.Faces.Count; f++)
        {
            if (f == lastFace) continue;
            var face = room.Faces[f];
            var side = face.InwardNormal.Dot(image - face.Vertices[0]);
            if (face.IsScreen ? Math.Abs(side) <= Tolerance : side <= Tolerance) continue;

            var reflected = Reflect(image, face);
            faces.Add(f);
            images.Add(reflected);

            // where the straight line from the image to the far end leaves this, the last face it reflects from
            var n = face.InwardNormal;
            var denominator = n.Dot(reflected - end);
            if (Math.Abs(denominator) > 1e-12)
            {
                var near = end + (reflected - end) * (n.Dot(face.Vertices[0] - end) / denominator);
                bool? inSight = null; // the specular path itself, which the image sum has if the corner leaves it in sight
                for (var e = 0; e < corners.Edges.Count; e++)
                {
                    if (corners.ReflectedTurn(corners.Edges[e], reflected, end, near) is not { } turn) continue;
                    inSight ??= IsVisible(room, start, end, faces, images, out _, out _, out _);
                    if (inSight.Value) break;
                    if (!room.IsClearOfWalls(turn, end) || !seen.Add((Key(reflected), e, backwards))) continue;
                    if (!IsVisible(room, start, turn, faces, images, out var first, out var points, out var screened)) continue;

                    var route = (turn - reflected).Length + (end - turn).Length;
                    var bend = new CornerDiffraction.Path(turn, route, Math.Max(0, route - (end - reflected).Length));
                    if (room.HasScreens && room.ScreensCrossed(turn, end).Count > 0) (screened ??= new List<(Vec3, Vec3)>()).Add((turn, end));
                    // points run from the turn back to the start through the reflections
                    arrivals.Add(backwards
                        ? new ImageArrival(route, Enumerable.Reverse(faces).ToArray(), turn, (points[^1] - points[^2]).Normalized(),
                            withPaths ? points : null, screened, bend)
                        : new ImageArrival(route, faces.ToArray(), first, (end - turn).Normalized(),
                            withPaths ? Enumerable.Reverse(points).ToArray() : null, screened, bend));
                }
            }

            if (faces.Count < maxOrder)
                SearchBent(room, start, end, reflected, f, faces, images, maxOrder, corners, backwards, withPaths, arrivals, seen);

            faces.RemoveAt(faces.Count - 1);
            images.RemoveAt(images.Count - 1);
        }
    }

    private static (long, long, long) Key(Vec3 p) =>
        ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));

    /// <summary>Mirror image of a point in a face's plane.</summary>
    private static Vec3 Reflect(Vec3 point, RoomFace face)
    {
        var n = face.InwardNormal;
        return point - n * (2 * n.Dot(point - face.Vertices[0]));
    }

    /// <summary>
    /// Walks back from the receiver to the source through the image chain. For
    /// each image, the line to it must cross that image's face inside the
    /// polygon, and each crossing becomes the start of the next leg. In a
    /// non-convex room every leg must then be clear of the walls.
    /// </summary>
    private static bool IsVisible(Room3D room, Vec3 source, Vec3 receiver, List<int> faces, List<Vec3> images, out Vec3 firstReflection,
        out Vec3[] points, out List<(Vec3 From, Vec3 To)>? screened)
    {
        firstReflection = default;
        screened = null;
        points = new Vec3[faces.Count + 2];
        points[0] = receiver;
        var x = receiver;
        for (var j = faces.Count - 1; j >= 0; j--)
        {
            var face = room.Faces[faces[j]];
            var target = images[j];
            var n = face.InwardNormal;
            var denominator = n.Dot(target - x);
            if (Math.Abs(denominator) < 1e-12) return false;

            // t = 0 is allowed: the leg starts on this face's plane when the path passes through an edge.
            var t = n.Dot(face.Vertices[0] - x) / denominator;
            if (t < -Tolerance || t > 1 + Tolerance) return false;

            var crossing = x + (target - x) * t;
            if (!room.FaceContains(faces[j], crossing)) return false;
            x = crossing;
            points[faces.Count - j] = crossing;
        }
        points[faces.Count + 1] = source;
        firstReflection = x;

        if (room.IsConvex && !room.HasScreens) return true;

        // A non-convex room can put a crossing behind a face's plane, or a wall in the way of a leg; a screen's path must stay on one side of it.
        for (var j = 0; j < faces.Count; j++)
        {
            var face = room.Faces[faces[j]];
            var crossing = points[faces.Count - j];
            var before = face.InwardNormal.Dot(points[faces.Count - j + 1] - crossing); // the point the sound came from, towards the source
            var after = face.InwardNormal.Dot(points[faces.Count - j - 1] - crossing);  // the point it goes to, towards the receiver
            if (face.IsScreen ? before * after < 0 : before < -Tolerance || after < -Tolerance) return false;
            // a wall reflection just where a screen meets the wall, from one side of the screen to the other
            if (!face.IsScreen && room.HasScreens && room.PassesThroughScreenAt(crossing, points[faces.Count - j + 1], points[faces.Count - j - 1])) return false;
        }
        for (var leg = 0; leg < points.Length - 1; leg++)
        {
            if (!room.IsClearOfWalls(points[leg], points[leg + 1])) return false;
            if (room.HasScreens && room.ScreensCrossed(points[leg], points[leg + 1]).Count > 0)
                (screened ??= new List<(Vec3, Vec3)>()).Add((points[leg + 1], points[leg]));
        }
        return true;
    }
}
