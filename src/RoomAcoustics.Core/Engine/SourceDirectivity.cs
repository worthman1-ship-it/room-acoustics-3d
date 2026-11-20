namespace RoomAcoustics.Core;

/// <summary>
/// The source's radiation pattern: relative energy per unit direction. The
/// pattern is normalised so its average over the sphere is 1, which keeps the
/// total radiated power the same as for an omnidirectional source.
/// </summary>
/// <remarks>
/// Rays are launched in uniform random directions and each carries its
/// direction's gain. This is unbiased with no rejection step, so there is no
/// bound on the pattern to get wrong. Direct and image paths are weighted by
/// the gain of their departure direction.
/// </remarks>
public sealed class SourceDirectivity
{
    private const int NormalisationPoints = 20_000;

    private readonly Func<Vec3, double> _pattern;
    private readonly double _scale;
    private readonly SourceDirectivity[]? _bands;

    /// <param name="pattern">Relative energy in a unit direction. Must be non-negative and not zero everywhere.</param>
    public SourceDirectivity(Func<Vec3, double> pattern)
    {
        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        var mean = MeanOverSphere(pattern);
        if (!(mean > 0) || !double.IsFinite(mean))
            throw new ArgumentException("The pattern must be positive somewhere on the sphere.", nameof(pattern));
        _scale = 1 / mean;
    }

    private SourceDirectivity(SourceDirectivity[] bands)
    {
        // Used where no band is asked for; the 1 kHz pattern stands for the source.
        _pattern = bands[4]._pattern;
        _scale = bands[4]._scale;
        _bands = bands;
    }

    /// <summary>
    /// A source whose pattern changes with frequency: one directivity for each octave band, 63 Hz to 8 kHz. Each is
    /// normalised on its own, so the radiated power is the same in every band.
    /// </summary>
    public static SourceDirectivity PerBand(IReadOnlyList<SourceDirectivity> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count != 8) throw new ArgumentException("There must be one directivity for each of the eight octave bands.", nameof(bands));
        return new SourceDirectivity(bands.ToArray());
    }

    /// <summary>
    /// The pattern to use in an octave band (0 for 63 Hz up to 7 for 8 kHz): this one for a source with the same pattern
    /// everywhere, or the band's own for one made with <see cref="PerBand"/>.
    /// </summary>
    public SourceDirectivity ForBand(int bandIndex)
    {
        if (bandIndex < 0 || bandIndex > 7) throw new ArgumentOutOfRangeException(nameof(bandIndex), "The band index must be 0 to 7.");
        return _bands is null ? this : _bands[bandIndex];
    }

    /// <summary>Equal energy in every direction.</summary>
    public static SourceDirectivity Omnidirectional { get; } = new(_ => 1.0);

    /// <summary>A cardioid pattern, 1 + cos θ about <paramref name="axis"/>: maximum forward, zero straight back.</summary>
    public static SourceDirectivity Cardioid(Vec3 axis)
    {
        var unit = axis.Normalized();
        return new SourceDirectivity(d => 1 + d.Dot(unit));
    }

    /// <summary>Relative energy (average over the sphere is 1) in a unit direction.</summary>
    public double Gain(Vec3 unitDirection) => _pattern(unitDirection) * _scale;

    /// <summary>
    /// Mean of the pattern over the sphere, on a Fibonacci lattice. The lattice is deterministic
    /// and nearly uniform, so the normalisation does not depend on any random seed.
    /// </summary>
    private static double MeanOverSphere(Func<Vec3, double> pattern)
    {
        var golden = Math.PI * (3 - Math.Sqrt(5));
        var sum = 0.0;
        for (var i = 0; i < NormalisationPoints; i++)
        {
            var z = 1 - 2 * (i + 0.5) / NormalisationPoints;
            var r = Math.Sqrt(1 - z * z);
            var phi = i * golden;
            sum += pattern(new Vec3(r * Math.Cos(phi), r * Math.Sin(phi), z));
        }
        return sum / NormalisationPoints;
    }
}
