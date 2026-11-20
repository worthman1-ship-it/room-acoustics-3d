namespace RoomAcoustics.Core;

/// <summary>
/// Core logarithmic level arithmetic and A-weighting, ported from the original
/// "acoustic_calculations.vb" engine (R. King, Worthman Software, 2009) -
/// functions dba/dbAdd/dbAve. Formulas and the A-weighting correction table
/// are preserved exactly from that source.
/// </summary>
public static class AcousticMath
{
    /// <summary>
    /// A-weighting correction per octave band (63 Hz - 8 kHz), as used in the
    /// original dba() function.
    /// </summary>
    private static readonly double[] AWeightingCorrection =
    {
        -26.2, -16.1, -8.6, -3.2, 0, 1.2, 1, -1.1
    };

    /// <summary>C-weighting correction per octave band (63 Hz - 8 kHz), IEC 61672 values rounded to 0.1 dB.</summary>
    private static readonly double[] CWeightingCorrection =
    {
        -0.8, -0.2, 0, 0, 0, -0.2, -0.8, -3.0
    };

    /// <summary>Logarithmically add any number of sound levels (dB or dB(A)).</summary>
    public static double LevelAdd(params double[] levels) => LevelAdd((IEnumerable<double>)levels);

    public static double LevelAdd(IEnumerable<double> levels)
    {
        var sum = levels.Sum(level => Math.Pow(10, level / 10));
        return 10 * Math.Log10(sum);
    }

    /// <summary>Logarithmic (energy) average of a set of levels - LevelAdd minus 10*log10(count).</summary>
    public static double LevelAverage(params double[] levels) => LevelAverage((IEnumerable<double>)levels);

    public static double LevelAverage(IEnumerable<double> levels)
    {
        var list = levels as IReadOnlyCollection<double> ?? levels.ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one level is required.", nameof(levels));
        return LevelAdd(list) - 10 * Math.Log10(list.Count);
    }

    /// <summary>
    /// Logarithmically subtract one level from a total (energy subtraction) -
    /// e.g. removing a known background noise level from a measured total.
    /// The algebraic inverse of LevelAdd; not present in the original code but
    /// follows directly from the same log-energy relationship.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="levelToRemove"/> is greater than or equal to
    /// <paramref name="totalLevel"/>, which gives no physically valid result.
    /// </exception>
    public static double LevelSubtract(double totalLevel, double levelToRemove)
    {
        var difference = Math.Pow(10, totalLevel / 10) - Math.Pow(10, levelToRemove / 10);
        if (difference <= 0)
            throw new ArgumentException(
                "The level to remove must be lower than the total level for a valid result.",
                nameof(levelToRemove));
        return 10 * Math.Log10(difference);
    }

    /// <summary>
    /// Single-figure dB(A) from an octave-band spectrum of sound pressure
    /// levels, using the standard A-weighting correction per band.
    /// </summary>
    public static double DbA(OctaveSpectrum spectrum)
    {
        var sum = 0.0;
        for (var i = 0; i < 8; i++)
            sum += Math.Pow(10, (spectrum[i] + AWeightingCorrection[i]) / 10);
        return 10 * Math.Log10(sum);
    }

    /// <summary>Single-figure dB(C) from an octave-band spectrum of sound pressure levels.</summary>
    public static double DbC(OctaveSpectrum spectrum)
    {
        var sum = 0.0;
        for (var i = 0; i < 8; i++)
            sum += Math.Pow(10, (spectrum[i] + CWeightingCorrection[i]) / 10);
        return 10 * Math.Log10(sum);
    }
}
