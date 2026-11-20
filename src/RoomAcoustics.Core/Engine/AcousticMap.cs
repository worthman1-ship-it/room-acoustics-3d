namespace RoomAcoustics.Core;

/// <summary>
/// The room-acoustic metrics at each of a set of receiver positions. <see cref="Responses"/> are the energy impulse
/// responses they came from, for drawing decay curves and reflectograms; <see cref="Calculate"/> keeps them without the
/// arrival-direction arrays, which the metrics have already used.
/// </summary>
public sealed record AcousticMap(IReadOnlyList<Vec3> Receivers, IReadOnlyList<RoomAcousticMetrics> Metrics,
    IReadOnlyList<ImpulseResponse>? Responses = null)
{
    /// <summary>
    /// Runs one ray set against all receivers, then computes the ISO 3382 metrics at each. The result is
    /// ready to be drawn as a colour map (for example one frequency band of T30 over a horizontal grid).
    /// </summary>
    public static AcousticMap Calculate(Room3D room, Vec3 source, IReadOnlyList<Vec3> receivers, RayTraceOptions? options = null) =>
        Calculate(room, ExtendedSource.Point(source), receivers, options);

    /// <summary>The same for a source made of several elements (a line, a surface, an array); see <see cref="ExtendedSourceTracer"/>.</summary>
    public static AcousticMap Calculate(Room3D room, ExtendedSource source, IReadOnlyList<Vec3> receivers, RayTraceOptions? options = null)
    {
        var responses = ExtendedSourceTracer.TraceReceivers(room, source, receivers, options);
        var metrics = responses.Select(RoomAcousticsCalculator.Calculate).ToList();
        var kept = responses.Select(r => r with { LateralEnergy = null, LateralAmplitudeEnergy = null }).ToList();
        return new AcousticMap(receivers, metrics, kept);
    }

    /// <summary>
    /// One value per receiver, for a metric and an octave band (for example
    /// <c>map.Values(m => m.T30S, 1000)</c>). Values are in the metric's own units.
    /// </summary>
    public double[] Values(Func<RoomAcousticMetrics, OctaveSpectrum> metric, int frequencyHz) =>
        Metrics.Select(m => metric(m).AtFrequency(frequencyHz)).ToArray();

    /// <summary>One value per receiver for a single-figure measure with no octave band (a weighted level, the STI).</summary>
    public double[] Values(Func<RoomAcousticMetrics, double> metric) => Metrics.Select(metric).ToArray();
}
