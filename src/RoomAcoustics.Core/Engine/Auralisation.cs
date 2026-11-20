using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace RoomAcoustics.Core;

/// <summary>
/// Turns the energy impulse responses from the ray tracer into sound: a sampled
/// impulse response per ear, convolved with a dry signal, for stereo playback.
/// </summary>
/// <remarks>
/// <para>
/// The ray tracer gives energy per time bin, not phase, so the impulse response
/// is synthesised. For each octave band, Gaussian noise is band-limited to the
/// band (edges at f/√2 and f√2) and multiplied by √(energy / (bin width × sample
/// rate)) for the bin each sample falls in. The result has the right energy
/// envelope in every band. It is a statistical model of the room's response, not
/// the exact waveform: transient detail, and the phase of the direct sound, are
/// not reproduced, and the band edges are sharp.
/// </para>
/// <para>
/// Stereo comes from two ear positions traced as separate receivers, so the
/// interaural time and level differences of the direct sound and the early
/// reflections come from the geometry, not from a head-related transfer function.
/// The two channels are scaled by the same factor, which keeps those differences.
/// </para>
/// </remarks>
public static class Auralisation
{
    /// <summary>
    /// A mono impulse response at <paramref name="sampleRateHz"/>, synthesised from an energy impulse response.
    /// The seed fixes the noise, so the same input gives the same sound.
    /// </summary>
    public static double[] SynthesizeImpulseResponse(ImpulseResponse ir, int sampleRateHz, int seed = 1)
    {
        ArgumentNullException.ThrowIfNull(ir);
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sample rate must be positive.");

        var binWidth = ir.BinWidthS;
        var binCount = ir.Energy[0].Length;
        var sampleCount = (int)Math.Ceiling(binCount * binWidth * sampleRateHz);
        var fftSize = NextPowerOfTwo(sampleCount);

        var random = new Random(seed);
        var white = new Complex[fftSize];
        for (var i = 0; i < fftSize; i++)
            white[i] = new Complex(Gaussian(random), 0);
        Fourier.Forward(white, FourierOptions.Matlab);

        var impulse = new double[sampleCount];
        var energyScale = binWidth * sampleRateHz;
        for (var band = 0; band < 8; band++)
        {
            var centre = OctaveSpectrum.Frequencies[band];
            var low = centre / Math.Sqrt(2);
            var high = centre * Math.Sqrt(2);

            // Keep the white-noise spectrum inside the band, then return to the time domain.
            var spectrum = new Complex[fftSize];
            for (var k = 0; k < fftSize; k++)
            {
                var frequency = Math.Abs((k <= fftSize / 2 ? k : k - fftSize) * (double)sampleRateHz / fftSize);
                if (frequency >= low && frequency < high) spectrum[k] = white[k];
            }
            Fourier.Inverse(spectrum, FourierOptions.Matlab);

            // Unit variance in the band, so the envelope alone sets the level.
            var variance = spectrum.Sum(c => c.Real * c.Real) / fftSize;
            if (variance <= 0) continue;
            var noiseScale = 1 / Math.Sqrt(variance);

            var energy = ir.Energy[band];
            for (var i = 0; i < sampleCount; i++)
            {
                var bin = (int)(i / (double)sampleRateHz / binWidth);
                if (bin >= binCount) continue;
                impulse[i] += spectrum[i].Real * noiseScale * Math.Sqrt(energy[bin] / energyScale);
            }
        }
        return impulse;
    }

    /// <summary>
    /// The signal at another sample rate, by windowed-sinc interpolation: each output sample is the input summed under a sinc kernel of
    /// 16 zero crossings, shaped by a Hann window, with its cutoff at the lower of the two Nyquist frequencies so that nothing above the new
    /// Nyquist frequency folds back when the rate falls. The input is taken to be zero outside its ends. The length is scaled by the ratio
    /// of the rates and rounded.
    /// </summary>
    public static double[] Resample(double[] signal, int fromRateHz, int toRateHz)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (fromRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(fromRateHz), "The sample rate must be positive.");
        if (toRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(toRateHz), "The sample rate must be positive.");
        if (fromRateHz == toRateHz) return (double[])signal.Clone();

        const int zeroCrossings = 16;
        var result = new double[(int)Math.Round(signal.Length * (double)toRateHz / fromRateHz)];
        var step = fromRateHz / (double)toRateHz;                  // input samples per output sample
        var cutoff = Math.Min(1.0, toRateHz / (double)fromRateHz); // as a fraction of the input's Nyquist frequency
        var half = zeroCrossings / cutoff;                         // the kernel's half-width in input samples
        for (var j = 0; j < result.Length; j++)
        {
            var t = j * step;
            var sum = 0.0;
            var last = Math.Min(signal.Length - 1, (int)Math.Floor(t + half));
            for (var i = Math.Max(0, (int)Math.Ceiling(t - half)); i <= last; i++)
            {
                var d = i - t;
                var u = cutoff * d;
                var sinc = Math.Abs(u) < 1e-12 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
                sum += signal[i] * cutoff * sinc * 0.5 * (1 + Math.Cos(Math.PI * d / half));
            }
            result[j] = sum;
        }
        return result;
    }

    /// <summary>Linear convolution by FFT. The output has length <c>signal.Length + impulse.Length - 1</c>.</summary>
    public static double[] Convolve(double[] signal, double[] impulse)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(impulse);
        if (signal.Length == 0 || impulse.Length == 0) return Array.Empty<double>();

        var length = signal.Length + impulse.Length - 1;
        var size = NextPowerOfTwo(length);
        var a = new Complex[size];
        var b = new Complex[size];
        for (var i = 0; i < signal.Length; i++) a[i] = signal[i];
        for (var i = 0; i < impulse.Length; i++) b[i] = impulse[i];

        Fourier.Forward(a, FourierOptions.Matlab);
        Fourier.Forward(b, FourierOptions.Matlab);
        for (var i = 0; i < size; i++) a[i] *= b[i];
        Fourier.Inverse(a, FourierOptions.Matlab);

        var result = new double[length];
        for (var i = 0; i < length; i++) result[i] = a[i].Real;
        return result;
    }

    /// <summary>
    /// Left and right channels: the dry signal convolved with each ear's impulse response. Both channels are
    /// scaled by the same factor so that the largest sample is <paramref name="peak"/>, which keeps the
    /// interaural level difference.
    /// </summary>
    public static (double[] Left, double[] Right) RenderStereo(double[] dry, double[] leftImpulse, double[] rightImpulse, double peak = 0.5) =>
        RenderStereo(dry, leftImpulse, rightImpulse, peak, out _);

    /// <summary>As <see cref="RenderStereo(double[], double[], double[], double)"/>, also giving the factor the channels were scaled by.</summary>
    public static (double[] Left, double[] Right) RenderStereo(double[] dry, double[] leftImpulse, double[] rightImpulse, double peak, out double scale)
    {
        var left = Convolve(dry, leftImpulse);
        var right = Convolve(dry, rightImpulse);
        var largest = Math.Max(left.Length == 0 ? 0 : left.Max(Math.Abs), right.Length == 0 ? 0 : right.Max(Math.Abs));
        scale = 1;
        if (largest > 0)
        {
            scale = peak / largest;
            for (var i = 0; i < left.Length; i++) left[i] *= scale;
            for (var i = 0; i < right.Length; i++) right[i] *= scale;
        }
        return (left, right);
    }

    /// <summary>
    /// Left and right ear positions for a listener whose head is at <paramref name="head"/> and faces
    /// <paramref name="facing"/> (taken as horizontal; z is up). The default spacing is a typical head width.
    /// </summary>
    public static (Vec3 Left, Vec3 Right) EarPositions(Vec3 head, Vec3 facing, double spacingM = 0.17)
    {
        if (spacingM <= 0) throw new ArgumentOutOfRangeException(nameof(spacingM), "The ear spacing must be positive.");
        var right = facing.Cross(new Vec3(0, 0, 1)).Normalized();
        return (head - right * (spacingM / 2), head + right * (spacingM / 2));
    }

    private static double Gaussian(Random random)
    {
        // Box-Muller transform.
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static int NextPowerOfTwo(int n)
    {
        var size = 1;
        while (size < n) size <<= 1;
        return size;
    }
}
