namespace RoomAcoustics.Core;

/// <summary>
/// The sound that reaches a point behind a free-standing screen by bending round its edges. Each free edge of the screen (its top, its sides; not an edge
/// standing on the floor or against a wall) gives a path over the shortest route round it, from the path difference δ = |SP| + |PR| - |SR| and the
/// Fresnel number N = 2δ/λ. Each is cut by the Kurze-Anderson approximation of a thin barrier's insertion loss, 5 + 20 lg(√(2πN) / tanh √(2πN)) dB
/// (Maekawa's curve in closed form), and their energies add. A screen never cuts more than <see cref="MaxAttenuationDb"/>; a panel with no free edge
/// (one filling the room's cross-section) lets nothing past. Several screens in a line multiply. The edge paths themselves are not checked for walls or
/// other screens in the way. A screen that sound also passes through (a partition with a sound reduction R) adds τ = 10^(-R/10) of the face the line
/// crosses to what bends round it, so a partition filling the room's cross-section lets through what its R says, and one with a door in it that and what
/// bends round the door's edges.
/// </summary>
public static class ScreenDiffraction
{
    /// <summary>The most one screen cuts, dB: the usual limit for a single thin barrier (as in ISO 9613-2).</summary>
    public const double MaxAttenuationDb = 20;

    /// <summary>The Kurze-Anderson insertion loss of a thin barrier, dB, at Fresnel number N (0 at grazing: 5 dB).</summary>
    public static double InsertionLossDb(double fresnel)
    {
        if (fresnel <= 1e-9) return 5;
        var x = Math.Sqrt(2 * Math.PI * fresnel);
        return 5 + 20 * Math.Log10(x / Math.Tanh(x));
    }

    /// <summary>The path difference, m, of the shortest route from <paramref name="from"/> to <paramref name="to"/> by way of a point on the edge from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static double PathDifference(Vec3 from, Vec3 to, Vec3 a, Vec3 b)
    {
        var edge = b - a;
        var length = edge.Length;
        var u = edge * (1 / length);
        double tFrom = (from - a).Dot(u), tTo = (to - a).Dot(u);
        double dFrom = (from - (a + u * tFrom)).Length, dTo = (to - (a + u * tTo)).Length;
        // unfolded about the edge, the shortest route is straight: it meets the edge where it divides the two feet in the ratio of the distances
        var t = dFrom + dTo < 1e-12 ? (tFrom + tTo) / 2 : (tFrom * dTo + tTo * dFrom) / (dFrom + dTo);
        var p = a + u * Math.Clamp(t, 0, length);
        return (from - p).Length + (p - to).Length - (from - to).Length;
    }

    /// <summary>The share of energy one screen lets through to a point behind it, at a wavelength (see the class remarks).</summary>
    public static double PanelFactor(ScreenPanel panel, Vec3 from, Vec3 to, double wavelengthM)
    {
        if (panel.Edges.Count == 0) return 0;
        var sum = 0.0;
        foreach (var (a, b) in panel.Edges)
            sum += Math.Pow(10, -InsertionLossDb(2 * PathDifference(from, to, a, b) / wavelengthM) / 10);
        return Math.Clamp(sum, Math.Pow(10, -MaxAttenuationDb / 10), 1);
    }

    /// <summary>
    /// The share of energy that gets from one point to another past every screen in the straight line between them (1 when none is), in the octave band
    /// <paramref name="band"/> for what passes through a screen (-1 for none).
    /// </summary>
    public static double EnergyFactor(Room3D room, Vec3 from, Vec3 to, double wavelengthM, int band = -1)
    {
        var factor = 1.0;
        foreach (var (screen, face) in room.ScreenFacesCrossed(from, to))
        {
            var through = band < 0 ? 0 : room.Faces[face].Finish.Transmission(band);
            factor *= Math.Min(1, PanelFactor(room.Screens[screen], from, to, wavelengthM) + through);
        }
        return factor;
    }
}
