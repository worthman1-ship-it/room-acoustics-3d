using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace RoomAcoustics.Core;

/// <summary>
/// Ambisonic impulse responses from the ray tracer, of order 1 to 3, in the AmbiX convention: (order + 1)² channels in ACN order (W, Y, Z, X, then the
/// second order's five and the third's seven) with SN3D weighting. A plane wave s from the unit direction (x, y, z) gives each channel s times its real
/// spherical harmonic there (<see cref="Harmonics"/>): W = s, Y = s·y, Z = s·z, X = s·x at first order; x along the room's length, z up.
/// </summary>
/// <remarks>
/// The tracer gives each time bin's energy and, with <see cref="RayTraceOptions.ArrivalDirections"/>, the energy-weighted sum of the directions its sound
/// comes from. As in DirAC (Pulkki, 2007), each bin's sound is split by the length r of its mean direction: r of its energy is a plane wave from that
/// direction, and 1 - r (the diffuseness) is a diffuse field, put into every channel as independent noise, 1 / (2n + 1) of W's energy in each channel of
/// order n, as a diffuse field gives SN3D channels (a third each in X, Y and Z). A bin holding one arrival (the direct sound, an early reflection) is then a
/// plane wave from where it came; the late tail, from every way, is mostly diffuse. Each bin has one mean direction, so two arrivals in the same
/// millisecond from different ways are one plane wave between them and a diffuse part: higher orders sharpen the image of each arrival, not the
/// separation of arrivals that close. The waveform itself is band-limited noise shaped to each octave band's energy, as for the binaural rendering
/// (<see cref="Auralisation.SynthesizeImpulseResponse"/>), so it is a statistical model of the response, not its exact waveform.
/// </remarks>
public static class Ambisonics
{
    /// <summary>The highest order offered.</summary>
    public const int MaxOrder = 3;

    /// <summary>
    /// The real spherical harmonics with SN3D normalisation, in ACN order, up to <paramref name="order"/>, at the unit direction (x, y, z): the gains of a
    /// plane wave from there in each channel.
    /// </summary>
    public static double[] Harmonics(Vec3 u, int order)
    {
        double x = u.X, y = u.Y, z = u.Z;
        double s3 = Math.Sqrt(3), s15 = Math.Sqrt(15), s58 = Math.Sqrt(5.0 / 8), s38 = Math.Sqrt(3.0 / 8);
        var all = new[]
        {
            1,
            y, z, x,
            s3 * x * y, s3 * y * z, (3 * z * z - 1) / 2, s3 * x * z, s3 / 2 * (x * x - y * y),
            s58 * y * (3 * x * x - y * y), s15 * x * y * z, s38 * y * (5 * z * z - 1), z * (5 * z * z - 3) / 2,
            s38 * x * (5 * z * z - 1), s15 / 2 * z * (x * x - y * y), s58 * x * (x * x - 3 * y * y),
        };
        return all.Take((order + 1) * (order + 1)).ToArray();
    }

    /// <summary>The order of the ACN channel <paramref name="acn"/>.</summary>
    private static int OrderOf(int acn) => (int)Math.Floor(Math.Sqrt(acn));

    /// <summary>The AmbiX channels of an impulse response traced with arrival directions, at <paramref name="sampleRateHz"/>, up to <paramref name="order"/>.</summary>
    public static double[][] Synthesize(ImpulseResponse ir, int sampleRateHz, int seed = 1, int order = 1)
    {
        ArgumentNullException.ThrowIfNull(ir);
        var from = ir.ArrivalFrom ?? throw new ArgumentException("The response was traced without the directions its sound comes from.", nameof(ir));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sample rate must be positive.");
        if (order < 1 || order > MaxOrder) throw new ArgumentOutOfRangeException(nameof(order), $"The order must be 1 to {MaxOrder}.");

        var binWidth = ir.BinWidthS;
        var binCount = ir.Energy[0].Length;
        var sampleCount = (int)Math.Ceiling(binCount * binWidth * sampleRateHz);
        var count = (order + 1) * (order + 1);
        var channels = Enumerable.Range(0, count).Select(_ => new double[sampleCount]).ToArray();
        var energyScale = binWidth * sampleRateHz;
        var diffuseGain = Enumerable.Range(0, count).Select(k => 1 / Math.Sqrt(2 * OrderOf(k) + 1)).ToArray();
        // independent noises: the plane wave's (0), and the diffuse field's in each channel; first order keeps the noises it always had (W 1, X 2, Y 3, Z 4)
        var noiseOf = Enumerable.Range(0, count).Select(k => k switch { 0 => 1, 1 => 3, 2 => 4, 3 => 2, _ => k + 1 }).ToArray();
        var noises = Enumerable.Range(0, count + 1).Select(k => BandNoise(sampleCount, sampleRateHz, seed * 7919 + k)).ToArray();
        for (var band = 0; band < 8; band++)
        {
            var energy = ir.Energy[band];
            double[] fx = from[0][band], fy = from[1][band], fz = from[2][band];
            var plane = noises[0][band];
            for (var i = 0; i < sampleCount; i++)
            {
                var bin = (int)(i / (double)sampleRateHz / binWidth);
                if (bin >= binCount || energy[bin] <= 0) continue;
                var e = energy[bin];
                var mean = new Vec3(fx[bin] / e, fy[bin] / e, fz[bin] / e);
                var r = Math.Min(1, mean.Length);
                var gains = Harmonics(r > 0 ? mean * (1 / mean.Length) : new Vec3(0, 0, 0), order);
                var amplitude = Math.Sqrt(e / energyScale);
                var directional = plane[i] * amplitude * Math.Sqrt(r);
                var diffuse = amplitude * Math.Sqrt(1 - r);
                for (var k = 0; k < count; k++)
                    channels[k][i] += directional * gains[k] + noises[noiseOf[k]][band][i] * diffuse * diffuseGain[k];
            }
        }
        return channels;
    }

    /// <summary>
    /// The B-format impulse response at a point: the source traced with arrival directions to a receiver there (its radius the options', at most
    /// 0.3 m, so that early arrivals stay apart), synthesised to <paramref name="order"/>, and scaled so that the largest sample of any channel is <paramref name="peak"/>.
    /// </summary>
    public static double[][] Render(Room3D room, ExtendedSource source, Vec3 point, int sampleRateHz, RayTraceOptions? options = null, double peak = 0.5, int order = 1)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(source);
        if (sampleRateHz < 8000 || sampleRateHz > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sample rate must be between 8000 and 192000 Hz.");
        if (!room.Contains(point)) throw new ArgumentException("The listening point must be inside the room.", nameof(point));
        options ??= new RayTraceOptions();
        options = options with { ArrivalDirections = true, ReceiverRadiusM = Math.Min(options.ReceiverRadiusM, 0.3) };
        var response = ExtendedSourceTracer.TraceReceivers(room, source, new[] { point }, options)[0];
        var channels = Synthesize(response, sampleRateHz, options.Seed, order);
        var top = channels.Max(c => c.Length == 0 ? 0 : c.Max(Math.Abs));
        if (top > 0) foreach (var c in channels) for (var i = 0; i < c.Length; i++) c[i] *= peak / top;
        return channels;
    }

    /// <summary>Unit-variance Gaussian noise limited to each octave band (edges at f/√2 and f√2), one signal per band.</summary>
    private static double[][] BandNoise(int sampleCount, int sampleRateHz, int seed)
    {
        var fftSize = 1;
        while (fftSize < sampleCount) fftSize <<= 1;
        var random = new Random(seed);
        var white = new Complex[fftSize];
        for (var i = 0; i < fftSize; i++)
        {
            // Box-Muller
            var u1 = 1 - random.NextDouble();
            white[i] = new Complex(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * random.NextDouble()), 0);
        }
        Fourier.Forward(white, FourierOptions.Matlab);
        var bands = new double[8][];
        for (var band = 0; band < 8; band++)
        {
            double centre = OctaveSpectrum.Frequencies[band], low = centre / Math.Sqrt(2), high = centre * Math.Sqrt(2);
            var spectrum = new Complex[fftSize];
            for (var k = 0; k < fftSize; k++)
            {
                var frequency = Math.Abs((k <= fftSize / 2 ? k : k - fftSize) * (double)sampleRateHz / fftSize);
                if (frequency >= low && frequency < high) spectrum[k] = white[k];
            }
            Fourier.Inverse(spectrum, FourierOptions.Matlab);
            var variance = spectrum.Sum(c => c.Real * c.Real) / fftSize;
            var scale = variance > 0 ? 1 / Math.Sqrt(variance) : 0;
            bands[band] = Enumerable.Range(0, sampleCount).Select(i => spectrum[i].Real * scale).ToArray();
        }
        return bands;
    }
}
