namespace RoomAcoustics.Core;

/// <summary>
/// The open-plan office single numbers of ISO 3382-3 (2012 and 2022) from a line of positions at increasing distance from one omnidirectional source. At each
/// position the speech level is normal unisex speech (Table 1, 1 m in front of the source in a free field) taken there by the room's strength G: Lp,S = Lp,S,1m
/// + G - 20, as Annex B of the 2022 edition derives it from an impulse response; the STI is the full IEC 60268-16 method with that speech, the background noise
/// and male weighting (5.5 of the 2022 edition).
/// </summary>
public static class OpenPlanOffice
{
    /// <summary>
    /// Normal unisex speech 1 m from an omnidirectional source in a free field, 125 Hz to 8 kHz (Table 1, both editions; 57.4 dB(A)). The standard has no 63 Hz,
    /// which the STI uses only as the masker of the 125 Hz band: it is a placeholder 12 dB below 125 Hz.
    /// </summary>
    public static readonly OctaveSpectrum SpeechAt1mDb = new(37.9, 49.9, 54.3, 58.0, 52.0, 44.8, 38.8, 33.5);

    private static readonly double[] AWeightingDb = { -26.2, -16.1, -8.6, -3.2, 0, 1.2, 1.0, -1.1 };

    /// <summary>The level the standard's comfort distance is taken at, dB(A) (2022, 3.4).</summary>
    public const double ComfortLevelDbA = 45;
    /// <summary>The STI the distraction distance (0.50) and the privacy distance (0.20, 2012 only) are taken at.</summary>
    public const double DistractionSti = 0.5, PrivacySti = 0.2;

    /// <summary>The standard's speech at a position whose strength is <paramref name="strengthGDb"/>: Lp,S,1m + G - 20 in each band (G is 20 dB at 1 m in a free field).</summary>
    public static OctaveSpectrum SpeechAt(OctaveSpectrum strengthGDb) =>
        new(Enumerable.Range(0, 8).Select(b => SpeechAt1mDb[b] + strengthGDb[b] - 20).ToArray());

    /// <summary>The A-weighted level of octave levels over 125 Hz to 8 kHz, the bands ISO 3382-3 sums (its Formula 5); NaN if none is a number.</summary>
    public static double AWeightedDb(OctaveSpectrum levels)
    {
        var terms = Enumerable.Range(1, 7).Select(b => levels[b] + AWeightingDb[b]).Where(double.IsFinite).ToList();
        return terms.Count == 0 ? double.NaN : 10 * Math.Log10(terms.Sum(l => Math.Pow(10, l / 10)));
    }

    /// <summary>The STI at a position: the standard's speech there, the background noise and the room's modulation transfer (band 1 to 7, modulation frequency), male weighting.</summary>
    public static double Sti(OctaveSpectrum strengthGDb, OctaveSpectrum noise, Func<int, double, double> modulationTransfer) =>
        SpeechTransmissionIndex.Calculate(SpeechAt(strengthGDb), noise, modulationTransfer, SpeechWeighting.Male);

    /// <summary>One measurement position: its distance from the source, the A-weighted speech level there and the STI.</summary>
    public sealed record Position(double DistanceM, double SpeechDbA, double Sti);

    /// <summary>
    /// The single numbers, NaN where they cannot be worked out: the spatial decay rate D2,S per doubling of distance and the speech level at 4 m (from the
    /// positions 2 to 16 m from the source), the comfort distance rC where speech falls below 45 dB(A), the distraction and privacy distances rD and rP where the
    /// STI falls below 0.50 and 0.20 (from every position more than 1 m away), and the STI at the nearest of those. A distance outside the positions' range
    /// is extrapolated, as the standard says it then is.
    /// </summary>
    public sealed record Result(
        double SpatialDecayDb, double SpeechAt4mDbA, double ComfortDistanceM, double DistractionDistanceM, double PrivacyDistanceM, double NearestSti,
        int DecayPositions, int StiPositions);

    /// <summary>The single numbers from the positions (see <see cref="Result"/>).</summary>
    public static Result Evaluate(IReadOnlyList<Position> positions)
    {
        // D2,S and Lp,A,S,4m: least squares of the speech level against log2 of the distance, positions 2 to 16 m (Formulas 6 and 7)
        var decay = positions.Where(p => p.DistanceM >= 2 && p.DistanceM <= 16 && double.IsFinite(p.SpeechDbA)).ToList();
        var (levelAt1m, perDoubling) = Fit(decay.Select(p => Math.Log2(p.DistanceM)).ToList(), decay.Select(p => p.SpeechDbA).ToList());
        var d2s = -perDoubling;
        var at4m = levelAt1m + perDoubling * 2;
        // rC = 2^((Lp,A,S,4m - 45 + 2 D2,S) / D2,S) (Formula 8): where the line reaches 45 dB(A); none if the speech does not fall with distance
        var comfort = d2s > 0 ? Math.Pow(2, (at4m - ComfortLevelDbA + 2 * d2s) / d2s) : double.NaN;

        // rD and rP: a straight line through the STI against the distance itself, every position beyond the 1 m reference
        var far = positions.Where(p => p.DistanceM > 1 && double.IsFinite(p.Sti)).OrderBy(p => p.DistanceM).ToList();
        var (stiAt0, stiPerMetre) = Fit(far.Select(p => p.DistanceM).ToList(), far.Select(p => p.Sti).ToList());
        double Reach(double sti) => stiPerMetre < 0 ? (sti - stiAt0) / stiPerMetre : double.NaN;

        return new Result(d2s, at4m, comfort, Reach(DistractionSti), Reach(PrivacySti), far.Count > 0 ? far[0].Sti : double.NaN, decay.Count, far.Count);
    }

    /// <summary>The least-squares line y = a + b x; NaN for both unless there are two different x.</summary>
    internal static (double A, double B) Fit(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 2) return (double.NaN, double.NaN);
        double mx = x.Average(), my = y.Average();
        var sxx = x.Sum(v => (v - mx) * (v - mx));
        if (sxx < 1e-12) return (double.NaN, double.NaN);
        var b = x.Zip(y, (u, v) => (u - mx) * (v - my)).Sum() / sxx;
        return (my - b * mx, b);
    }
}
