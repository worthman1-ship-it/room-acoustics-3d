namespace RoomAcoustics.Core;

/// <summary>
/// The energy reaching a receiver, gathered by how far to the side it comes from, in the two windows of ISO 3382-1's interaural cross-correlation:
/// <c>Early</c> is 0 to 80 ms after the first arrival and <c>Late</c> 80 ms to 1 s. Each is eight arrays (one per octave band) of
/// <see cref="InterauralCoherence.Cells"/> cells, cell k holding the energy that comes from a lateral angle in its degree, from -90° (straight from the
/// right) to +90° (straight from the left); the angle is between where the sound comes from and the plane square to the receiver's lateral axis.
/// </summary>
public sealed record InterauralEnergy(IReadOnlyList<double[]> Early, IReadOnlyList<double[]> Late);

/// <summary>
/// The interaural cross-correlation coefficient (IACC) of a listener whose head faces the source, worked out from where the energy comes from rather
/// than from two ear signals. Each arrival reaches the two ears as the same sound, scaled by the spherical head's shadow for each ear and delayed between
/// them by Woodworth's path round the head (<see cref="HeadShadow"/>); arrivals are incoherent with one another, as the ray tracer's energies are. So the
/// interaural cross-correlation function of a window is
/// IACF(τ) = Σ E_k g_k ρ(τ - Δ_k) / √(Σ E_k P_L,k · Σ E_k P_R,k), with P the ears' power gains, g = √(P_L P_R), Δ the delay of the right ear after the left,
/// and ρ the normalised autocorrelation of noise filling the octave band from f/√2 to f√2, ρ(τ) = (sin 2πf₂τ - sin 2πf₁τ) / (2π(f₂ - f₁)τ).
/// IACC is the largest |IACF(τ)| for τ within ±1 ms (ISO 3382-1 Annex A).
/// </summary>
/// <remarks>
/// A head facing the source has the arrivals' lateral angle as everything the two ears tell apart (a sphere's ears see front and back alike), which is why
/// the energy is gathered by that angle alone. The head is a rigid sphere with no pinnae and no torso, so a single arrival gives an IACC of 1 from any
/// direction: only several arrivals from different directions lower it, which is what the coefficient is meant to show.
/// </remarks>
public static class InterauralCoherence
{
    /// <summary>The number of cells of lateral angle, one a degree.</summary>
    public const int Cells = 180;

    /// <summary>The early window's end after the first arrival, s, and the late window's.</summary>
    public const double EarlyEndS = 0.08, LateEndS = 1.0;

    private const double LagLimitS = 0.001, LagStepS = 0.00001, SpeedOfSoundMs = 343;

    /// <summary>The cell for sound coming from a direction whose cosine to the left-ear axis is <paramref name="cosToLeft"/>.</summary>
    public static int Cell(double cosToLeft)
    {
        var angle = Math.Asin(Math.Clamp(cosToLeft, -1, 1));
        return Math.Clamp((int)((angle + Math.PI / 2) / Math.PI * Cells), 0, Cells - 1);
    }

    /// <summary>The cosine to the left-ear axis of the middle of cell <paramref name="cell"/>.</summary>
    public static double CellCosine(int cell) => Math.Sin(-Math.PI / 2 + (cell + 0.5) * Math.PI / Cells);

    /// <summary>The normalised autocorrelation of noise filling the octave band whose centre is <paramref name="centreHz"/>, at lag τ.</summary>
    public static double BandAutocorrelation(double centreHz, double lagS)
    {
        double low = centreHz / Math.Sqrt(2), high = centreHz * Math.Sqrt(2);
        if (Math.Abs(lagS) < 1e-12) return 1;
        return (Math.Sin(2 * Math.PI * high * lagS) - Math.Sin(2 * Math.PI * low * lagS)) / (2 * Math.PI * (high - low) * lagS);
    }

    /// <summary>IACC in each octave band from a window's cells, band by band (<see cref="InterauralEnergy"/>).</summary>
    public static OctaveSpectrum Bands(IReadOnlyList<double[]> cells) => new(Enumerable.Range(0, 8).Select(b => Iacc(cells[b], b)).ToArray());

    /// <summary>IACC in octave band <paramref name="band"/> (0 for 63 Hz) from the energy in each cell; NaN when there is none.</summary>
    public static double Iacc(IReadOnlyList<double> cells, int band)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count != Cells) throw new ArgumentException($"There must be {Cells} cells.", nameof(cells));
        double left = 0, right = 0;
        var weights = new List<(double Weight, double DelayS)>();
        for (var k = 0; k < Cells; k++)
        {
            if (!(cells[k] > 0)) continue;
            var c = CellCosine(k);
            double pl = HeadShadow.PowerGain(band, c), pr = HeadShadow.PowerGain(band, -c);
            left += cells[k] * pl;
            right += cells[k] * pr;
            weights.Add((cells[k] * Math.Sqrt(pl * pr), (HeadShadow.PathAroundHeadM(-c) - HeadShadow.PathAroundHeadM(c)) / SpeedOfSoundMs));
        }
        if (!(left > 0 && right > 0)) return double.NaN;

        var centre = OctaveSpectrum.Frequencies[band];
        var best = 0.0;
        // every 10 us across the ±1 ms, and each arrival's own delay, where one arrival on its own peaks
        var steps = (int)Math.Round(LagLimitS / LagStepS);
        var lags = Enumerable.Range(-steps, 2 * steps + 1).Select(i => i * LagStepS).Concat(weights.Select(w => w.DelayS).Where(d => Math.Abs(d) <= LagLimitS));
        foreach (var lag in lags)
        {
            var sum = 0.0;
            foreach (var (weight, delay) in weights) sum += weight * BandAutocorrelation(centre, lag - delay);
            best = Math.Max(best, Math.Abs(sum));
        }
        return Math.Min(1, best / Math.Sqrt(left * right));
    }
}
