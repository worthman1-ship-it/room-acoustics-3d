namespace RoomAcoustics.Core;

/// <summary>
/// The single-number (broadband) values of ISO 3382-1, each the mean of the octave bands the standard averages it over (ISO 3382-1:2009 Table A.1), leaving
/// out bands that cannot be worked out (NaN if none can). Reverberation times (T20, T30, EDT), centre time, D50, the clarities C80 and C50 (C50 is tied to D50
/// by its Equation A.12, so it takes D50's bands) and the strengths (G, Gearly, Glate) are the mean of 500 and 1000 Hz; the lateral measures LF80 and LFC80 of
/// 125 to 1000 Hz, and LJ the energy mean of the same bands (its Equation A.17); and Gade's stage support ST_early, ST_late and ST_total and early ensemble level EEL of 250 to 2000 Hz. The lateral measures are NaN where the response carries no arrival directions (a statistical estimate has none).
/// The bass ratio (T30 at 125 and 250 Hz over T30 at 500 and 1000 Hz) and brilliance ratio (2000 and 4000 Hz over 500 and 1000 Hz) are Beranek's single numbers, and the
/// echo criteria are the worst (largest) value in the 500, 1000 and 2000 Hz bands, since the criterion is a warning, not an average.
/// IACC E3, L3 and A3 are the early, late and whole-response interaural cross-correlation coefficients averaged over 500, 1000 and 2000 Hz (Hidaka, Beranek
/// and Okano), and the binaural quality index is 1 - IACC E3; NaN where the response was traced without them.
/// </summary>
public sealed record BroadbandMetrics(
    double T30S, double T20S, double EdtS, double C50Db, double C80Db, double D50, double CentreTimeS,
    double StrengthGDb, double EarlyStrengthGDb, double LateStrengthGDb,
    double LateralFraction80, double LateralFractionCoefficient80, double LateLateralLevelDb,
    double StageSupportEarlyDb, double StageSupportLateDb,
    double BassRatio = double.NaN, double Brilliance = double.NaN, double EchoCriterionSpeech = double.NaN, double EchoCriterionMusic = double.NaN,
    double DirectStrengthGDb = double.NaN, double DirectToReverberantDb = double.NaN,
    double StageSupportTotalDb = double.NaN, double EarlyEnsembleLevelDb = double.NaN,
    double IaccE3 = double.NaN, double IaccL3 = double.NaN, double BinauralQualityIndex = double.NaN, double IaccA3 = double.NaN)
{
    private static readonly int[] Mid = { 3, 4 };                // 500, 1000 Hz
    private static readonly int[] Echo = { 3, 4, 5 };            // 500, 1000, 2000 Hz, for the echo criteria
    private static readonly int[] Lateral = { 1, 2, 3, 4 };      // 125 to 1000 Hz
    private static readonly int[] Support = { 2, 3, 4, 5 };      // 250 to 2000 Hz

    /// <summary>The largest of the bands that are numbers (NaN if none is).</summary>
    public static double Worst(OctaveSpectrum spectrum, params int[] bands)
    {
        var values = bands.Select(b => spectrum[b]).Where(double.IsFinite).ToList();
        return values.Count == 0 ? double.NaN : values.Max();
    }

    /// <summary>Beranek's bass ratio, (T125 + T250) / (T500 + T1000).</summary>
    public static double BassRatioOf(OctaveSpectrum t) => (t[1] + t[2]) / (t[3] + t[4]);

    /// <summary>Beranek's brilliance ratio, (T2000 + T4000) / (T500 + T1000).</summary>
    public static double BrillianceOf(OctaveSpectrum t) => (t[5] + t[6]) / (t[3] + t[4]);

    /// <summary>The energy mean of the given bands that are numbers, in dB: 10 lg of the mean of 10^(L/10), as ISO 3382-1 averages LJ.</summary>
    public static double EnergyMean(OctaveSpectrum spectrum, params int[] bands)
    {
        var values = bands.Select(b => spectrum[b]).Where(double.IsFinite).ToList();
        return values.Count == 0 ? double.NaN : 10 * Math.Log10(values.Average(v => Math.Pow(10, v / 10)));
    }

    /// <summary>The mean of the given bands (indices into the eight octave bands, 0 for 63 Hz) that are numbers.</summary>
    public static double Mean(OctaveSpectrum spectrum, params int[] bands)
    {
        var values = bands.Select(b => spectrum[b]).Where(double.IsFinite).ToList();
        return values.Count == 0 ? double.NaN : values.Average();
    }

    public static BroadbandMetrics From(RoomAcousticMetrics m) => new(
        Mean(m.T30S, Mid), Mean(m.T20S, Mid), Mean(m.EarlyDecayTimeS, Mid), Mean(m.C50Db, Mid), Mean(m.C80Db, Mid), Mean(m.D50, Mid), Mean(m.CentreTimeS, Mid),
        Mean(m.StrengthGDb, Mid), Mean(m.EarlyStrengthGDb, Mid), Mean(m.LateStrengthGDb, Mid),
        Mean(m.LateralFraction80, Lateral), Mean(m.LateralFractionCoefficient80, Lateral), EnergyMean(m.LateLateralLevelLJDb, Lateral),
        Mean(m.StageSupportEarlyDb, Support), Mean(m.StageSupportLateDb, Support),
        BassRatioOf(m.T30S), BrillianceOf(m.T30S), Worst(m.EchoCriterionSpeech, Echo), Worst(m.EchoCriterionMusic, Echo),
        m.DirectStrengthGDb is { } direct ? Mean(direct, Mid) : double.NaN, m.DirectToReverberantDb is { } ratio ? Mean(ratio, Mid) : double.NaN,
        m.StageSupportTotalDb is { } total ? Mean(total, Support) : double.NaN, m.EarlyEnsembleLevelDb is { } ensemble ? Mean(ensemble, Support) : double.NaN,
        m.IaccEarly is { } early ? Mean(early, Echo) : double.NaN, m.IaccLate is { } late ? Mean(late, Echo) : double.NaN,
        m.IaccEarly is { } e3 ? 1 - Mean(e3, Echo) : double.NaN, m.IaccAll is { } all ? Mean(all, Echo) : double.NaN);

    /// <summary>From the statistical equations, where one decay rate (Eyring's time) is every reverberation time.</summary>
    public static BroadbandMetrics From(StatisticalResult s) => new(
        Mean(s.EyringT60S, Mid), Mean(s.EyringT60S, Mid), Mean(s.EyringT60S, Mid), Mean(s.C50Db, Mid), Mean(s.C80Db, Mid), Mean(s.D50, Mid), Mean(s.CentreTimeS, Mid),
        Mean(s.StrengthGDb, Mid), Mean(s.EarlyStrengthGDb, Mid), Mean(s.LateStrengthGDb, Mid),
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        BassRatioOf(s.EyringT60S), BrillianceOf(s.EyringT60S));
}
