namespace RoomAcoustics.Core;

/// <summary>One element of an <see cref="ExtendedSource"/>.</summary>
/// <param name="Position">Where the element radiates from.</param>
/// <param name="Weight">Its share of the source's power, relative to the other elements.</param>
/// <param name="Directivity">The element's own pattern; null uses the one in the trace options.</param>
/// <param name="DelayS">How long after time zero the element radiates, in seconds: a delay line in front of its loudspeaker. Zero or more.</param>
/// <param name="BandGains">
/// The element's power in each of the eight octave bands (63 Hz to 8 kHz), as linear factors that multiply <paramref name="Weight"/>, for sources
/// whose spectra differ: a band's share of the response goes by the elements' power in that band. Null is the same in every band.
/// </param>
public sealed record SourcePoint(Vec3 Position, double Weight = 1, SourceDirectivity? Directivity = null, double DelayS = 0, IReadOnlyList<double>? BandGains = null);

/// <summary>
/// A source made of several point elements that radiate incoherently, so their energies add: a line of loudspeakers, a
/// machine face, a window that lets sound out. Each element is a point source with a share of the total power.
/// </summary>
/// <remarks>
/// Adding energy rather than pressure means no interference between elements is modelled, which is right for the
/// reverberant field and for sources whose elements are not in step, and gives the 3 dB per doubling of distance of an
/// incoherent line near it. It does not reproduce the lobes of a coherent array. An element may be delayed (<see cref="SourcePoint.DelayS"/>),
/// which moves its energy later in time and nothing more: the arrival times of a delay-stepped array are modelled, not its interference.
/// </remarks>
public sealed class ExtendedSource
{
    /// <summary>The most elements a source may have; each costs a set of image-source paths per receiver.</summary>
    public const int MaxPoints = 64;

    public IReadOnlyList<SourcePoint> Points { get; }

    public ExtendedSource(IEnumerable<SourcePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Points = points.ToArray();
        if (Points.Count == 0) throw new ArgumentException("A source needs at least one element.", nameof(points));
        if (Points.Count > MaxPoints) throw new ArgumentException($"A source can have at most {MaxPoints} elements.", nameof(points));
        if (Points.Any(p => !(p.Weight > 0) || !double.IsFinite(p.Weight)))
            throw new ArgumentException("Each element's weight must be positive.", nameof(points));
        if (Points.Any(p => !(p.DelayS >= 0) || !double.IsFinite(p.DelayS)))
            throw new ArgumentException("Each element's delay must be zero or more.", nameof(points));
        if (Points.Any(p => p.BandGains is { } g && (g.Count != 8 || g.Any(v => !(v > 0) || !double.IsFinite(v)))))
            throw new ArgumentException("Each element's band gains must be eight positive numbers.", nameof(points));
    }

    /// <summary>
    /// Each element's share of the total power in each octave band: <c>[element][band]</c>, the shares of a band adding to 1. With no band gains
    /// it is the element's weight over the total weight in every band.
    /// </summary>
    public double[][] SharesByBand()
    {
        double Power(SourcePoint p, int band) => p.Weight * (p.BandGains?[band] ?? 1);
        var totals = Enumerable.Range(0, 8).Select(b => Points.Sum(p => Power(p, b))).ToArray();
        return Points.Select(p => Enumerable.Range(0, 8).Select(b => Power(p, b) / totals[b]).ToArray()).ToArray();
    }

    /// <summary>Whether any element has its own spectrum.</summary>
    public bool HasBandGains => Points.Any(p => p.BandGains is not null);

    /// <summary>Whether any element is delayed.</summary>
    public bool HasDelays => Points.Any(p => p.DelayS > 0);

    /// <summary>
    /// The delay of each element of a line of <paramref name="count"/> that steps by <paramref name="stepS"/> from one element to the next, in
    /// seconds, counting from the start of the line: 0, step, 2 step and so on. A negative step delays the start of the line instead, so the
    /// delays are never negative: the last element has none.
    /// </summary>
    public static double[] StepDelays(int count, double stepS)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "A line needs at least one element.");
        if (!double.IsFinite(stepS)) throw new ArgumentOutOfRangeException(nameof(stepS), "The delay step must be a number.");
        return Enumerable.Range(0, count).Select(i => stepS >= 0 ? i * stepS : (count - 1 - i) * -stepS).ToArray();
    }

    /// <summary>The centre of the elements, weighted by each one's share of the power (averaged over the octave bands).</summary>
    public Vec3 Centre
    {
        get
        {
            var shares = SharesByBand();
            var sum = Vec3.Zero;
            for (var k = 0; k < Points.Count; k++) sum += Points[k].Position * shares[k].Average();
            return sum;
        }
    }

    public bool IsPoint => Points.Count == 1;

    /// <summary>An ordinary point source.</summary>
    public static ExtendedSource Point(Vec3 position, SourceDirectivity? directivity = null) =>
        new(new[] { new SourcePoint(position, 1, directivity) });

    /// <summary>
    /// A straight line of <paramref name="count"/> elements of equal power, each at the middle of an equal part of the
    /// line from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public static ExtendedSource Line(Vec3 from, Vec3 to, int count, SourceDirectivity? directivity = null, IReadOnlyList<double>? delaysS = null)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "A line needs at least one element.");
        CheckDelays(count, delaysS);
        return new ExtendedSource(Enumerable.Range(0, count)
            .Select(i => new SourcePoint(from + (to - from) * ((i + 0.5) / count), 1, directivity, delaysS?[i] ?? 0)));
    }

    private static void CheckDelays(int count, IReadOnlyList<double>? delaysS)
    {
        if (delaysS is not null && delaysS.Count != count)
            throw new ArgumentException($"A line of {count} elements needs {count} delays, but {delaysS.Count} were given.", nameof(delaysS));
    }

    /// <summary>
    /// A straight line of <paramref name="count"/> elements of equal power, as <see cref="Line"/>, but with each element's own pattern from
    /// <paramref name="directivityOf"/> (given the element's index from <paramref name="from"/> towards <paramref name="to"/>): a line array
    /// whose loudspeakers are aimed differently. An element given null uses the pattern in the trace options.
    /// </summary>
    public static ExtendedSource AimedLine(Vec3 from, Vec3 to, int count, Func<int, SourceDirectivity?> directivityOf,
        IReadOnlyList<double>? delaysS = null)
    {
        ArgumentNullException.ThrowIfNull(directivityOf);
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "A line needs at least one element.");
        CheckDelays(count, delaysS);
        return new ExtendedSource(Enumerable.Range(0, count)
            .Select(i => new SourcePoint(from + (to - from) * ((i + 0.5) / count), 1, directivityOf(i), delaysS?[i] ?? 0)));
    }

    /// <summary>
    /// How far each element of a fanned line is aimed from the aim of its middle, in radians, given the angle between each element and the
    /// next: <paramref name="gaps"/>[k] is between element k and element k+1 (counting from the start of the line). If there are fewer than
    /// <c>count - 1</c> gaps the last one is repeated to the end, so a single angle fans the line evenly and a list that grows towards one end
    /// gives a J-shaped array; no gaps at all aim every element alike. The offsets are measured from the line's middle (between the two
    /// middle elements if the count is even), so the middle keeps the aim the line was given. More gaps than there are spaces between
    /// elements is an error.
    /// </summary>
    public static double[] FanOffsets(int count, IReadOnlyList<double> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "A line needs at least one element.");
        if (gaps.Count > Math.Max(1, count - 1))
            throw new ArgumentException($"A line of {count} elements has {count - 1} gaps between them, but {gaps.Count} angles were given.", nameof(gaps));

        var cumulative = new double[count];
        for (var i = 1; i < count; i++)
            cumulative[i] = cumulative[i - 1] + (gaps.Count == 0 ? 0 : gaps[Math.Min(i - 1, gaps.Count - 1)]);

        var middle = (count - 1) / 2.0;
        var lower = (int)Math.Floor(middle);
        var upper = Math.Min(lower + 1, count - 1);
        var centre = cumulative[lower] + (cumulative[upper] - cumulative[lower]) * (middle - lower);
        return cumulative.Select(c => c - centre).ToArray();
    }

    /// <summary>
    /// A rectangle of <paramref name="countAlong"/> by <paramref name="countUp"/> elements of equal power, each at the middle
    /// of an equal cell. The rectangle is centred on <paramref name="centre"/>, <paramref name="width"/> long along
    /// <paramref name="alongAxis"/> and <paramref name="height"/> along <paramref name="upAxis"/>.
    /// </summary>
    public static ExtendedSource Rectangle(Vec3 centre, Vec3 alongAxis, Vec3 upAxis, double width, double height, int countAlong, int countUp,
        SourceDirectivity? directivity = null)
    {
        if (countAlong < 1 || countUp < 1) throw new ArgumentOutOfRangeException(nameof(countAlong), "A surface needs at least one element each way.");
        if (!(width > 0) || !(height > 0)) throw new ArgumentOutOfRangeException(nameof(width), "The surface must have a width and a height.");
        var along = alongAxis.Normalized();
        var up = upAxis.Normalized();
        var points = new List<SourcePoint>();
        for (var j = 0; j < countUp; j++)
            for (var i = 0; i < countAlong; i++)
                points.Add(new SourcePoint(
                    centre + along * (((i + 0.5) / countAlong - 0.5) * width) + up * (((j + 0.5) / countUp - 0.5) * height), 1, directivity));
        return new ExtendedSource(points);
    }
}

/// <summary>Traces rays from every element of an <see cref="ExtendedSource"/> and combines the responses.</summary>
public static class ExtendedSourceTracer
{
    /// <summary>
    /// Impulse responses at several receivers for an extended source: each element is traced as a point source, with the
    /// options' rays shared between the elements in proportion to their power, and the responses are added with each
    /// element's share of the power. A source of one element gives exactly the result of
    /// <see cref="EnergyRayTracer.TraceReceivers"/>. <paramref name="earNormals"/> are as there.
    /// </summary>
    public static IReadOnlyList<ImpulseResponse> TraceReceivers(Room3D room, ExtendedSource source, IReadOnlyList<Vec3> receivers,
        RayTraceOptions? options = null, IReadOnlyList<Vec3>? earNormals = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new RayTraceOptions();

        if (source.IsPoint)
        {
            var only = source.Points[0];
            var traced = EnergyRayTracer.TraceReceivers(room, only.Position, receivers, options with { Directivity = only.Directivity ?? options.Directivity }, earNormals);
            return only.DelayS > 0 ? traced.Select(r => Delayed(r, only.DelayS)).ToList() : traced;
        }

        // each element's share of the power in each band; the rays are shared by the shares averaged over the bands, so an element that is loud in only
        // a few bands still gets rays for them
        var shares = source.SharesByBand();
        var parts = new List<(double[] Share, IReadOnlyList<ImpulseResponse> Responses)>();
        for (var k = 0; k < source.Points.Count; k++)
        {
            var element = source.Points[k];
            var share = shares[k];
            var rays = Math.Max(1, (int)Math.Round(options.RayCount * share.Average()));
            var own = options with { RayCount = rays, Seed = options.Seed + k, Directivity = element.Directivity ?? options.Directivity };
            var traced = EnergyRayTracer.TraceReceivers(room, element.Position, receivers, own, earNormals);
            parts.Add((share, element.DelayS > 0 ? traced.Select(r => Delayed(r, element.DelayS)).ToList() : traced));
        }

        var results = new ImpulseResponse[receivers.Count];
        for (var r = 0; r < receivers.Count; r++)
        {
            var first = parts[0].Responses[r];
            var energy = Combine(parts, r, x => x.Energy)!;
            var lateral = Combine(parts, r, x => x.LateralEnergy);
            var lateralAmplitude = Combine(parts, r, x => x.LateralAmplitudeEnergy);
            var shadowed = Combine(parts, r, x => x.ShadowedEnergy);
            // each element's direct sound, with its share of the power
            var direct = parts.All(p => p.Responses[r].DirectEnergy is not null)
                ? new OctaveSpectrum(Enumerable.Range(0, 8).Select(b => parts.Sum(p => p.Share[b] * p.Responses[r].DirectEnergy!.Value[b])).ToArray())
                : (OctaveSpectrum?)null;
            var from = first.ArrivalFrom is null ? null
                : Enumerable.Range(0, 3).Select(k => (IReadOnlyList<double[]>)Combine(parts, r, x => x.ArrivalFrom![k])!).ToArray();
            // each element's energy by direction, timed from its own first arrival (a delayed element's windows are not moved)
            var interaural = first.Interaural is null ? null
                : new InterauralEnergy(Combine(parts, r, x => x.Interaural!.Early)!, Combine(parts, r, x => x.Interaural!.Late)!);
            results[r] = new ImpulseResponse(first.BinWidthS, energy, first.ReferenceEnergy10m, lateral, lateralAmplitude, shadowed, direct, from, interaural);
        }
        return results;
    }

    /// <summary>
    /// The response moved later by <paramref name="delayS"/>: each bin's energy goes to the bins <c>delay / width</c> after it, split between the
    /// two it falls between when that is not a whole number of bins, so the total energy is kept (what runs past the end of the response is
    /// lost, as arrivals past the time limit are). The lateral and shadowed energies move with it.
    /// </summary>
    internal static ImpulseResponse Delayed(ImpulseResponse response, double delayS)
    {
        var bins = delayS / response.BinWidthS;
        var whole = (int)Math.Floor(bins);
        var fraction = bins - whole;

        IReadOnlyList<double[]>? Shift(IReadOnlyList<double[]>? bands) => bands?.Select(h =>
        {
            var shifted = new double[h.Length];
            for (var i = 0; i < h.Length; i++)
            {
                if (i + whole < h.Length) shifted[i + whole] += h[i] * (1 - fraction);
                if (fraction > 0 && i + whole + 1 < h.Length) shifted[i + whole + 1] += h[i] * fraction;
            }
            return shifted;
        }).ToArray();

        return response with
        {
            Energy = Shift(response.Energy)!,
            LateralEnergy = Shift(response.LateralEnergy),
            LateralAmplitudeEnergy = Shift(response.LateralAmplitudeEnergy),
            ShadowedEnergy = Shift(response.ShadowedEnergy),
            ArrivalFrom = response.ArrivalFrom?.Select(k => (IReadOnlyList<double[]>)Shift(k)!).ToArray(),
        };
    }

    private static IReadOnlyList<double[]>? Combine(List<(double[] Share, IReadOnlyList<ImpulseResponse> Responses)> parts, int receiver,
        Func<ImpulseResponse, IReadOnlyList<double[]>?> select)
    {
        if (select(parts[0].Responses[receiver]) is null) return null;
        var sum = new double[8][];
        for (var b = 0; b < 8; b++)
        {
            var bins = select(parts[0].Responses[receiver])![b].Length;
            sum[b] = new double[bins];
            foreach (var (share, responses) in parts)
            {
                var h = select(responses[receiver])![b];
                for (var i = 0; i < bins; i++) sum[b][i] += share[b] * h[i];
            }
        }
        return sum;
    }
}
