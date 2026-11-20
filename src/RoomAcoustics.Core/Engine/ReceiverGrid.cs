namespace RoomAcoustics.Core;

/// <summary>Receiver positions for mapping a room: a horizontal grid of points, each clear of every surface.</summary>
public static class ReceiverGrid
{
    /// <summary>
    /// Points on a horizontal plane at <paramref name="heightM"/>, one per cell of a square grid of side
    /// <paramref name="spacingM"/> (cell centres, starting half a cell in from the room's bounding box). A point is kept
    /// only if it is at least <paramref name="clearanceM"/> from every surface, so each receiver sphere fits inside the room.
    /// </summary>
    public static IReadOnlyList<Vec3> Horizontal(Room3D room, double heightM, double spacingM, double clearanceM)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (spacingM <= 0) throw new ArgumentOutOfRangeException(nameof(spacingM), "The grid spacing must be positive.");
        if (clearanceM < 0) throw new ArgumentOutOfRangeException(nameof(clearanceM), "The clearance cannot be negative.");

        var corners = room.Faces.SelectMany(f => f.Vertices).ToList();
        var minX = corners.Min(v => v.X);
        var maxX = corners.Max(v => v.X);
        var minY = corners.Min(v => v.Y);
        var maxY = corners.Max(v => v.Y);

        var points = new List<Vec3>();
        for (var x = minX + spacingM / 2; x <= maxX; x += spacingM)
            for (var y = minY + spacingM / 2; y <= maxY; y += spacingM)
            {
                var point = new Vec3(x, y, heightM);
                if (room.DistanceToBoundary(point) >= clearanceM)
                    points.Add(point);
            }

        if (points.Count == 0)
            throw new ArgumentException("No grid point is clear of the surfaces: reduce the spacing or the clearance, or raise the height.");
        return points;
    }

    /// <summary>
    /// Points a fixed vertical distance <paramref name="offsetM"/> above the upward-facing faces that <paramref name="onSurface"/> picks by name, one per cell of a
    /// square grid of side <paramref name="spacingM"/>, so over a raked or stepped surface the grid rises and falls with it. A cell with no such face beneath it is
    /// left out, as is one whose point is not at least <paramref name="clearanceM"/> from every surface; where a cell has several faces beneath it, the highest one
    /// with a clear point wins.
    /// </summary>
    public static IReadOnlyList<Vec3> OverSurface(Room3D room, Func<string, bool> onSurface, double offsetM, double spacingM, double clearanceM)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(onSurface);
        if (spacingM <= 0) throw new ArgumentOutOfRangeException(nameof(spacingM), "The grid spacing must be positive.");
        if (clearanceM < 0) throw new ArgumentOutOfRangeException(nameof(clearanceM), "The clearance cannot be negative.");

        // a face that can be stood above: it faces up, so it is not a wall or a ceiling
        var faces = Enumerable.Range(0, room.Faces.Count).Where(i => room.Faces[i].InwardNormal.Z > 0.05 && onSurface(room.Faces[i].Name)).ToList();
        if (faces.Count == 0) throw new ArgumentException("That surface faces up nowhere, so there is nothing to map above it. Choose a floor or another upward-facing surface.");

        var corners = room.Faces.SelectMany(f => f.Vertices).ToList();
        var minX = corners.Min(v => v.X);
        var minY = corners.Min(v => v.Y);
        var cellsX = (int)Math.Floor((corners.Max(v => v.X) - minX - spacingM / 2) / spacingM) + 1;
        var cellsY = (int)Math.Floor((corners.Max(v => v.Y) - minY - spacingM / 2) / spacingM) + 1;

        // each face is listed under the cells its footprint covers, so a cell tests only the faces near it
        var under = new Dictionary<(int, int), List<int>>();
        foreach (var f in faces)
        {
            var v = room.Faces[f].Vertices;
            var x0 = Math.Max(0, (int)Math.Floor((v.Min(c => c.X) - minX) / spacingM - 0.5));
            var x1 = Math.Min(cellsX - 1, (int)Math.Ceiling((v.Max(c => c.X) - minX) / spacingM - 0.5));
            var y0 = Math.Max(0, (int)Math.Floor((v.Min(c => c.Y) - minY) / spacingM - 0.5));
            var y1 = Math.Min(cellsY - 1, (int)Math.Ceiling((v.Max(c => c.Y) - minY) / spacingM - 0.5));
            for (var i = x0; i <= x1; i++)
                for (var j = y0; j <= y1; j++)
                {
                    if (!under.TryGetValue((i, j), out var list)) under[(i, j)] = list = new List<int>();
                    list.Add(f);
                }
        }

        var points = new List<Vec3>();
        for (var i = 0; i < cellsX; i++)
            for (var j = 0; j < cellsY; j++)
            {
                if (!under.TryGetValue((i, j), out var candidates)) continue;
                double x = minX + spacingM / 2 + i * spacingM, y = minY + spacingM / 2 + j * spacingM;
                var heights = new List<double>();
                foreach (var f in candidates)
                {
                    var face = room.Faces[f];
                    var z = face.Vertices[0].Z - (face.InwardNormal.X * (x - face.Vertices[0].X) + face.InwardNormal.Y * (y - face.Vertices[0].Y)) / face.InwardNormal.Z;
                    if (room.FaceContains(f, new Vec3(x, y, z))) heights.Add(z);
                }
                foreach (var z in heights.OrderByDescending(h => h))
                {
                    var point = new Vec3(x, y, z + offsetM);
                    if (room.DistanceToBoundary(point) < clearanceM) continue;
                    points.Add(point);
                    break;
                }
            }

        if (points.Count == 0)
            throw new ArgumentException("No point above that surface is clear of the room's surfaces: reduce the spacing or the clearance, or change the height above it.");
        return points;
    }
}
