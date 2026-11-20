using System.Globalization;

namespace RoomAcoustics.Core;

/// <summary>
/// Receivers through the volume of a room, for a "3-D matrix" of results: the plan grid of <see cref="ReceiverGrid"/> repeated at several heights.
/// </summary>
public static class RoomVolume
{
    /// <summary>The most heights one volume may have, to keep the number of receivers within reach of a run.</summary>
    public const int MaxHeights = 12;

    /// <summary>
    /// Reads heights in metres from text: numbers separated by commas, semicolons, spaces or line breaks, with a point as the decimal separator.
    /// Sorted, with repeats dropped. Throws <see cref="FormatException"/> for something that is not a number or is not finite, and <see cref="ArgumentException"/> for no
    /// heights, a negative height or more than <see cref="MaxHeights"/>.
    /// </summary>
    public static IReadOnlyList<double> ParseHeights(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Give at least one height.", nameof(text));
        var heights = new SortedSet<double>();
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || !double.IsFinite(h))
                throw new FormatException($"'{part}' is not a height in metres (use a point for decimals, and commas or spaces between heights).");
            if (h < 0) throw new ArgumentException($"A height cannot be negative ({part}).", nameof(text));
            heights.Add(Math.Round(h, 6));
        }
        if (heights.Count > MaxHeights) throw new ArgumentException($"At most {MaxHeights} heights.", nameof(text));
        return heights.ToList();
    }

    /// <summary>
    /// The receivers of a plan grid (see <see cref="ReceiverGrid.Horizontal"/>) at each of the heights, lowest height first. A height at which no
    /// grid point is clear of the surfaces (above the ceiling of a low part of the room, say) adds none; if no height gives any, the call throws
    /// <see cref="ArgumentException"/>. The heights that gave none are returned in <paramref name="emptyHeights"/>.
    /// </summary>
    public static IReadOnlyList<Vec3> Grid(Room3D room, IReadOnlyList<double> heightsM, double spacingM, double clearanceM, out IReadOnlyList<double> emptyHeights)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(heightsM);
        if (heightsM.Count == 0) throw new ArgumentException("Give at least one height.", nameof(heightsM));
        var points = new List<Vec3>();
        var empty = new List<double>();
        foreach (var h in heightsM.OrderBy(x => x))
        {
            try { points.AddRange(ReceiverGrid.Horizontal(room, h, spacingM, clearanceM)); }
            catch (ArgumentException) when (spacingM > 0 && clearanceM >= 0) { empty.Add(h); }
        }
        if (points.Count == 0)
            throw new ArgumentException("No grid point at any of those heights is clear of the surfaces: reduce the spacing or the clearance, or change the heights.");
        emptyHeights = empty;
        return points;
    }
}
