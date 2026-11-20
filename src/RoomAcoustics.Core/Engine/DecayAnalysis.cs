namespace RoomAcoustics.Core;

/// <summary>A straight line fitted to part of a decay curve: dB against seconds, and the reverberation time it implies.</summary>
/// <param name="SlopeDbPerS">Slope of the fitted line, negative for a decay.</param>
/// <param name="InterceptDb">The line's level at t = 0.</param>
/// <param name="ReverberationTimeS">Time for a 60 dB decay at that slope.</param>
public readonly record struct DecayFit(double SlopeDbPerS, double InterceptDb, double ReverberationTimeS)
{
    /// <summary>The fitted level at time <paramref name="timeS"/>.</summary>
    public double LevelAt(double timeS) => InterceptDb + SlopeDbPerS * timeS;
}

/// <summary>
/// The decay curve of an energy impulse response: the Schroeder backward integral, and the least-squares lines through it
/// that ISO 3382-1 uses for EDT, T20 and T30. <see cref="RoomAcousticsCalculator"/> takes its decay times from here, so the
/// curves drawn on screen are the ones the numbers come from.
/// </summary>
public static class DecayAnalysis
{
    /// <summary>
    /// Schroeder backward integral: for each bin i, the energy remaining from bin i to the end, in dB relative to the
    /// total. Bins are indexed by their left edge, so entry i is the decay curve at time i * dt. A bin with nothing left
    /// after it is negative infinity.
    /// </summary>
    public static double[] SchroederDb(double[] energy)
    {
        ArgumentNullException.ThrowIfNull(energy);
        var total = energy.Sum();
        var result = new double[energy.Length];
        var remaining = 0.0;
        for (var i = energy.Length - 1; i >= 0; i--)
        {
            remaining += energy[i];
            result[i] = remaining > 0 && total > 0 ? 10 * Math.Log10(remaining / total) : double.NegativeInfinity;
        }
        return result;
    }

    /// <summary>
    /// The line of least squares through the decay curve between <paramref name="upperDb"/> and <paramref name="lowerDb"/>
    /// (both negative or zero; upper is the less negative end), or null when fewer than two points lie in that range or the
    /// curve does not fall.
    /// </summary>
    public static DecayFit? Fit(double[] decayDb, double binWidthS, double upperDb, double lowerDb)
    {
        ArgumentNullException.ThrowIfNull(decayDb);
        var times = new List<double>();
        var levels = new List<double>();
        for (var i = 0; i < decayDb.Length; i++)
        {
            var level = decayDb[i];
            if (double.IsFinite(level) && level <= upperDb && level >= lowerDb)
            {
                times.Add(i * binWidthS);
                levels.Add(level);
            }
        }
        if (times.Count < 2) return null;

        var meanT = times.Average();
        var meanL = levels.Average();
        var numerator = 0.0;
        var denominator = 0.0;
        for (var i = 0; i < times.Count; i++)
        {
            numerator += (times[i] - meanT) * (levels[i] - meanL);
            denominator += (times[i] - meanT) * (times[i] - meanT);
        }
        var slope = numerator / denominator;
        return slope < 0 ? new DecayFit(slope, meanL - slope * meanT, -60 / slope) : null;
    }

    /// <summary>The early decay time's fit: 0 to -10 dB.</summary>
    public static DecayFit? EarlyDecayFit(double[] decayDb, double binWidthS) => Fit(decayDb, binWidthS, 0, -10);

    /// <summary>The T20 fit: -5 to -25 dB.</summary>
    public static DecayFit? T20Fit(double[] decayDb, double binWidthS) => Fit(decayDb, binWidthS, -5, -25);

    /// <summary>The T30 fit: -5 to -35 dB.</summary>
    public static DecayFit? T30Fit(double[] decayDb, double binWidthS) => Fit(decayDb, binWidthS, -5, -35);

    /// <summary>
    /// Each bin's energy in dB relative to the largest bin (the reflectogram's scale), with an empty bin and anything below
    /// <paramref name="floorDb"/> given as negative infinity. All negative infinity when there is no energy.
    /// </summary>
    public static double[] EnergyDb(double[] energy, double floorDb)
    {
        ArgumentNullException.ThrowIfNull(energy);
        var peak = energy.Length == 0 ? 0 : energy.Max();
        return energy.Select(e =>
        {
            if (e <= 0 || peak <= 0) return double.NegativeInfinity;
            var level = 10 * Math.Log10(e / peak);
            return level < floorDb ? double.NegativeInfinity : level;
        }).ToArray();
    }
}
