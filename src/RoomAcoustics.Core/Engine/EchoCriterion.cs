namespace RoomAcoustics.Core;

/// <summary>Which listening task the echo criterion is weighted for.</summary>
public enum EchoKind
{
    /// <summary>Speech: a short window, and the criterion applies to delays beyond 50 ms.</summary>
    Speech,

    /// <summary>Music: a longer window and a looser limit, applied to delays beyond 80 ms.</summary>
    Music,
}

/// <summary>
/// The largest value of the echo criterion and where it occurs.
/// </summary>
/// <param name="Value">The maximum of EK(τ) over the delays the criterion applies to.</param>
/// <param name="DelayS">The delay τ, from the first arrival, at which that maximum occurs.</param>
/// <param name="Limit">The value above which half of listeners hear a disturbing echo.</param>
/// <param name="Limit10Percent">The value above which one listener in ten does.</param>
public sealed record EchoCriterionResult(double Value, double DelayS, double Limit, double Limit10Percent)
{
    /// <summary>True when the criterion is above its 50 % limit: a disturbing echo is expected.</summary>
    public bool IsEcho => Value > Limit;

    /// <summary>True when it is above the 10 % limit: some listeners will hear an echo.</summary>
    public bool IsPerceptibleToSome => Value > Limit10Percent;
}

/// <summary>
/// The echo criterion of Dietsch and Kraak (1986). The centre time of the response up to a delay τ is
/// ts(τ) = ∫ t·|p|ⁿ dt / ∫ |p|ⁿ dt, both over 0 to τ, and the criterion is EK(τ) = (ts(τ+Δτ) − ts(τ)) / Δτ. A strong
/// reflection that arrives late and is not preceded by weaker ones moves the centre time sharply, which is what
/// the ear hears as a separate echo; a smooth decay moves it slowly.
/// </summary>
/// <remarks>
/// <para>
/// Speech uses n = 2/3, Δτ = 9 ms and a limit of 1.0 (0.9 for one listener in ten), looked at beyond 50 ms. Music uses
/// n = 1, Δτ = 14 ms and a limit of 1.8 (1.5), beyond 80 ms. Time is measured from the first arrival, so a longer path
/// from source to receiver does not change the result.
/// </para>
/// <para>
/// |p| is taken as the square root of the energy in a 1 ms bin, so the response is treated as one pressure sample per
/// bin. The authors recommend the 1 kHz octave band for speech and 1 to 2 kHz for music.
/// </para>
/// </remarks>
public static class EchoCriterion
{
    private static (double Exponent, double WindowS, double FirstDelayS, double Limit, double Limit10) Parameters(EchoKind kind) =>
        kind == EchoKind.Speech ? (2.0 / 3, 0.009, 0.05, 1.0, 0.9) : (1.0, 0.014, 0.08, 1.8, 1.5);

    /// <summary>
    /// EK(τ) for τ = 0, dt, 2dt... after the first arrival, in the energy response <paramref name="energy"/>
    /// (one value per bin of <paramref name="binWidthS"/>). A value is NaN where the criterion is not defined: at τ = 0,
    /// and where the window runs off the end of the response.
    /// </summary>
    public static double[] Curve(double[] energy, double binWidthS, EchoKind kind)
    {
        ArgumentNullException.ThrowIfNull(energy);
        if (binWidthS <= 0) throw new ArgumentOutOfRangeException(nameof(binWidthS), "The bin width must be positive.");

        var (exponent, windowS, _, _, _) = Parameters(kind);
        var window = Math.Max(1, (int)Math.Round(windowS / binWidthS));
        var onset = Array.FindIndex(energy, e => e > 0);
        if (onset < 0) return Array.Empty<double>();

        // Running sums of the weight |p|^n and of t times it, for the bins from the first arrival: entry k covers the first k.
        var count = energy.Length - onset;
        var weightSum = new double[count + 1];
        var timeSum = new double[count + 1];
        for (var j = 0; j < count; j++)
        {
            var weight = Math.Pow(Math.Max(energy[onset + j], 0), exponent / 2);
            weightSum[j + 1] = weightSum[j] + weight;
            timeSum[j + 1] = timeSum[j] + (j + 0.5) * binWidthS * weight;
        }

        var curve = new double[count];
        for (var k = 0; k < count; k++)
        {
            curve[k] = k >= 1 && k + window <= count && weightSum[k] > 0
                ? (timeSum[k + window] / weightSum[k + window] - timeSum[k] / weightSum[k]) / (window * binWidthS)
                : double.NaN;
        }
        return curve;
    }

    /// <summary>
    /// The largest EK(τ) over the delays the criterion applies to (beyond 50 ms for speech, 80 ms for music), or null when
    /// the response has nothing in it or ends before then.
    /// </summary>
    public static EchoCriterionResult? Evaluate(double[] energy, double binWidthS, EchoKind kind)
    {
        var curve = Curve(energy, binWidthS, kind);
        var (_, _, firstDelayS, limit, limit10) = Parameters(kind);

        var best = double.NaN;
        var bestIndex = -1;
        for (var k = 0; k < curve.Length; k++)
        {
            if (k * binWidthS < firstDelayS - 1e-12 || double.IsNaN(curve[k])) continue;
            if (bestIndex < 0 || curve[k] > best) { best = curve[k]; bestIndex = k; }
        }
        return bestIndex < 0 ? null : new EchoCriterionResult(best, bestIndex * binWidthS, limit, limit10);
    }

    /// <summary>The delay (seconds) beyond which the criterion is looked at: 50 ms for speech, 80 ms for music.</summary>
    public static double FirstDelayS(EchoKind kind) => Parameters(kind).FirstDelayS;

    /// <summary>The 50 % limit for the kind of signal: 1.0 for speech, 1.8 for music.</summary>
    public static double Limit(EchoKind kind) => Parameters(kind).Limit;

    /// <summary>The 10 % limit for the kind of signal: 0.9 for speech, 1.5 for music.</summary>
    public static double Limit10Percent(EchoKind kind) => Parameters(kind).Limit10;
}
