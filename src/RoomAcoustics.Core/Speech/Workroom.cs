namespace RoomAcoustics.Core;

/// <summary>
/// The spatial sound distribution of a workroom (ISO 14257:2001) from a line of positions at increasing distance from one omnidirectional source. At each
/// position the sound distribution value in an octave band is Dj = Lp,j - LW,j (Formula 1); the room's strength G is the level re the same source's free-field
/// level at 10 m, LW - 31 dB, so Dj = Gj - 31. The free-field reference curve is Dref = -20 lg(r / 1 m) - 11 dB (Formula 2). In each of the standard's three
/// regions of distance from the source (6.2: near, 1 m to d1; middle, d1 to d2; far, from d2; d1 = 5 m and d2 = 16 m) the curve gives DL2, its decay per
/// doubling of distance (6.3, Formula 5), DLf, its average excess over the reference curve (6.4.2, Formula 7), and DL'fr, the height of its regression line
/// over the reference curve at the region's conventional distance, 4 m, 10 m or 30 m (6.4.3, Formula 8).
/// </summary>
public static class Workroom
{
    /// <summary>The octave bands the standard measures, 125 Hz to 4 kHz: band indices 1 to 6 of an <see cref="OctaveSpectrum"/>.</summary>
    public const int FirstBand = 1, LastBand = 6;

    /// <summary>Pj, A-weighted pink noise in the bands 125 Hz to 4 kHz, for the frequency-normalized curve (Table 1).</summary>
    public static readonly double[] NormalisedSpectrumDb = { -16.1, -8.6, -3.2, 0, 1.2, 1.0 };

    /// <summary>The distances from the source, m, that divide the near, middle and far regions: the standard's typical d1 and d2 (6.2).</summary>
    public const double D1M = 5, D2M = 16;

    /// <summary>A region of distance from the source, from <see cref="FromM"/> to <see cref="ToM"/> m (both included), and its conventional distance for DL'fr.</summary>
    public sealed record Region(string Name, double FromM, double ToM, double AtM);

    /// <summary>The near region, 1 m to d1, dominated by the direct sound (used for small rooms); DL'fr at 4 m.</summary>
    public static readonly Region Near = new("near", 1, D1M, 4);

    /// <summary>The middle region, d1 to d2, the one for health and safety; DL'fr at 10 m.</summary>
    public static readonly Region Middle = new("middle", D1M, D2M, 10);

    /// <summary>The far region, from d2, where machines and fittings shape the curve; DL'fr at 30 m.</summary>
    public static readonly Region Far = new("far", D2M, double.PositiveInfinity, 30);

    /// <summary>The three regions, near to far.</summary>
    public static readonly IReadOnlyList<Region> Regions = new[] { Near, Middle, Far };

    /// <summary>
    /// The three regions for other values of d1 and d2, which the standard allows to suit the room (and wants reported); the conventional distances for
    /// DL'fr stay 4 m, 10 m and 30 m. d1 must be over 1 m, where the near region starts, and d2 beyond d1.
    /// </summary>
    public static IReadOnlyList<Region> RegionsFor(double d1M, double d2M)
    {
        if (!(d1M > 1) || !(d2M > d1M) || !double.IsFinite(d2M))
            throw new ArgumentException($"The workroom regions need d1 over 1 m and d2 beyond it (here {d1M:0.##} m and {d2M:0.##} m).");
        return d1M == D1M && d2M == D2M ? Regions
            : new[] { Near with { ToM = d1M }, Middle with { FromM = d1M, ToM = d2M }, Far with { FromM = d2M } };
    }

    /// <summary>The free-field reference curve at <paramref name="distanceM"/> metres, dB (Formula 2).</summary>
    public static double Reference(double distanceM) => -20 * Math.Log10(distanceM) - 11;

    /// <summary>The sound distribution value in each band, Dj = Gj - 31 dB, at a position whose strength is <paramref name="strengthGDb"/>.</summary>
    public static OctaveSpectrum Distribution(OctaveSpectrum strengthGDb) => new(Enumerable.Range(0, 8).Select(b => strengthGDb[b] - 31).ToArray());

    /// <summary>The frequency-normalized value: 10 lg sum 10^((Dj + Pj) / 10) - 6.2 dB over 125 Hz to 4 kHz (Formula 4); NaN if any band is not a number.</summary>
    public static double Normalised(OctaveSpectrum distribution) =>
        Energy(Enumerable.Range(FirstBand, LastBand - FirstBand + 1).Select(b => distribution[b] + NormalisedSpectrumDb[b - FirstBand])) - 6.2;

    /// <summary>
    /// The value for a source of sound power <paramref name="soundPowerDb"/> in each band (a machine's, say): 10 lg sum 10^((Dj + LW,j) / 10) - 10 lg sum
    /// 10^(LW,j / 10) over 125 Hz to 4 kHz (Formula 3), so Lp - LW of the whole spectrum; NaN if any band is not a number.
    /// </summary>
    public static double ForSpectrum(OctaveSpectrum distribution, IReadOnlyList<double> soundPowerDb)
    {
        var bands = Enumerable.Range(FirstBand, LastBand - FirstBand + 1).ToList();
        return Energy(bands.Select(b => distribution[b] + soundPowerDb[b])) - Energy(bands.Select(b => soundPowerDb[b]));
    }

    private static double Energy(IEnumerable<double> levels)
    {
        var list = levels.ToList();
        return list.All(double.IsFinite) ? 10 * Math.Log10(list.Sum(l => Math.Pow(10, l / 10))) : double.NaN;
    }

    /// <summary>
    /// DL2, DLf and DL'fr of one curve over a region. DL2 is -0.3 times the least-squares slope of D against lg(r / 1 m) (Formula 5). DLf is the excess
    /// D - Dref averaged over lg r by trapezoids between the positions in order of distance (Formula 7). DL'fr is the regression line's height over the
    /// reference curve at the region's conventional distance r, as Formula 8 writes it: the mean of D + 20 lg r + DL2 / lg 2 (mean of lg ri - lg r) + 11 dB.
    /// (Formula 5 takes lg 2 as 0.3 and Formula 8 does not, so a free field's DL'fr is a few hundredths of a dB off zero.) NaN unless at least two positions at
    /// different distances lie in the region; Positions is how many do.
    /// </summary>
    public sealed record Result(double DecayPerDoublingDb, double ExcessDb, double ExcessAtDb, int Positions);

    /// <summary>DL2, DLf and DL'fr over <paramref name="region"/> from each position's distance and value of D (see <see cref="Result"/>).</summary>
    public static Result Evaluate(IReadOnlyList<double> distancesM, IReadOnlyList<double> values, Region region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var used = distancesM.Zip(values).Where(p => p.First >= region.FromM && p.First <= region.ToM && double.IsFinite(p.Second)).OrderBy(p => p.First).ToList();
        if (used.Select(p => p.First).Distinct().Count() < 2) return new Result(double.NaN, double.NaN, double.NaN, used.Count);

        var r = used.Select(p => p.First).ToArray();
        var d = used.Select(p => p.Second).ToArray();
        var x = r.Select(Math.Log10).ToArray();
        var z = r.Length;
        var slope = (z * x.Zip(d, (a, b) => a * b).Sum() - d.Sum() * x.Sum()) / (z * x.Sum(a => a * a) - x.Sum() * x.Sum());
        var decay = -0.3 * slope;

        var excess = d.Select((di, i) => di - Reference(r[i])).ToArray();
        var area = Enumerable.Range(1, z - 1).Sum(i => (excess[i] + excess[i - 1]) * Math.Log10(r[i] / r[i - 1]));
        var average = area / (2 * Math.Log10(r[^1] / r[0]));

        var at = d.Average() + 20 * Math.Log10(region.AtM) + decay / Math.Log10(2) * (x.Average() - Math.Log10(region.AtM)) + 11;
        return new Result(decay, average, at, z);
    }
}
