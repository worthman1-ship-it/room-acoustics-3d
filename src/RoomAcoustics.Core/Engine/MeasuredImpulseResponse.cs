using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace RoomAcoustics.Core;

/// <summary>What the analysis of a measured impulse response found.</summary>
/// <param name="Metrics">The ISO 3382-1 parameters per octave band, from the same calculator as the simulation. G and the lateral measures are NaN: a recording has no free-field reference or arrival directions.</param>
/// <param name="DynamicRangeDb">Per octave band, the level of the response's peak above the noise floor at its end; infinity if the end is silent, zero if the band could not be used.</param>
/// <param name="DirectArrivalS">How long the recording ran before the direct sound (its largest sample), which is taken as time zero.</param>
/// <param name="DurationS">The length analysed, from the direct sound.</param>
/// <param name="SampleRateHz">The recording's sample rate.</param>
public sealed record MeasuredImpulseAnalysis(RoomAcousticMetrics Metrics, double[] DynamicRangeDb, double DirectArrivalS, double DurationS, int SampleRateHz);

/// <summary>
/// The room-acoustic parameters of an impulse response that was measured, not simulated: a mono recording is split into octave bands, its
/// energy is gathered into 1 ms bins, and <see cref="RoomAcousticsCalculator"/> works out the same parameters it does for the simulation, so a
/// measurement and a simulated receiver can be set side by side on the Measured tab.
/// </summary>
/// <remarks>
/// <para>
/// Time zero is the direct sound, taken as the largest sample. Each octave band (edges at f/√2 and f√2, as in
/// <see cref="Auralisation"/>) is cut out of the spectrum by a zero-phase filter with Butterworth-shaped edges (order 4, 24 dB per octave
/// beyond the edges, where the gain is 3 dB down). Sharp edges were tried and rejected: a brick-wall filter rings with a 1/t tail after the
/// direct sound that sets a floor near 60 dB under it in the low bands. Being zero-phase the filter rings before a sharp transient as much
/// as after it; anything before the direct sound is discarded before the filtering, so that cannot reach the decay.
/// </para>
/// <para>
/// The noise floor of a band is the mean energy of the last tenth of the recording. It is subtracted from the response, and the response is
/// cut off where its short-time average (11 ms, or four periods of the band if longer) first comes within 3 dB of the floor, so that the noise does not lengthen the decay. This is a simple
/// stand-in for the iterative method of Lundeby et al. (1995), which also finds the knee between decay and noise. A decay time is given
/// only if the band's dynamic range exceeds the decay's evaluation range by 10 dB (20 dB for EDT, 35 for T20, 45 for T30); the
/// clarity and centre-time figures do not need that. A band under 10 dB of dynamic range gives nothing.
/// </para>
/// <para>
/// At most the first 20 s from the direct sound are used. The 8 kHz band is cut short by the Nyquist frequency below a 22.6 kHz sample rate.
/// </para>
/// </remarks>
public static class MeasuredImpulseResponse
{
    /// <summary>The width of the energy bins handed to the calculator: 1 ms.</summary>
    public const double BinWidthS = 0.001;

    /// <summary>The longest stretch from the direct sound that is analysed.</summary>
    public const double MaxDurationS = 20;

    /// <summary>The shortest stretch from the direct sound that can be analysed.</summary>
    public const double MinDurationS = 0.2;

    /// <summary>The slowest sample rate accepted, below which most of the 4 kHz and 8 kHz bands do not exist.</summary>
    public const int MinSampleRateHz = 16000;

    /// <summary>The margin of dynamic range, in dB, that a decay time needs above the range it is fitted over.</summary>
    public const double DecayMarginDb = 10;

    private const int FilterOrder = 4;
    private const int SmoothingBins = 11;
    private const double SmoothingPeriods = 4;
    private const double UnusableBelowDb = 10;


    /// <summary>
    /// The parameters of a mono impulse response. Throws <see cref="ArgumentException"/> for a sample rate under
    /// <see cref="MinSampleRateHz"/>, a recording with no sound in it, or less than <see cref="MinDurationS"/> from the direct sound to its end.
    /// </summary>
    public static MeasuredImpulseAnalysis Analyse(IReadOnlyList<double> samples, int sampleRateHz)
    {
        var (x, peak) = Prepare(samples, sampleRateHz);
        var bandSignals = BandSignals(x, sampleRateHz);
        var energy = new double[8][];
        var dynamicRange = new double[8];
        for (var b = 0; b < 8; b++)
            (energy[b], dynamicRange[b]) = Decay(bandSignals[b], sampleRateHz, OctaveSpectrum.Frequencies[b]);

        var ir = new ImpulseResponse(BinWidthS, energy, OctaveSpectrum.Flat(0));
        var metrics = RoomAcousticsCalculator.Calculate(ir);
        metrics = metrics with
        {
            EarlyDecayTimeS = Needing(metrics.EarlyDecayTimeS, dynamicRange, 10 + DecayMarginDb),
            T20S = Needing(metrics.T20S, dynamicRange, 25 + DecayMarginDb),
            T30S = Needing(metrics.T30S, dynamicRange, 35 + DecayMarginDb),
        };
        return new MeasuredImpulseAnalysis(metrics, dynamicRange, peak / (double)sampleRateHz, x.Length / (double)sampleRateHz, sampleRateHz);
    }

    /// <summary>
    /// The part of a recording to analyse, from its largest sample (the direct sound) for at most <see cref="MaxDurationS"/>, and the index of that sample.
    /// Throws <see cref="ArgumentException"/> for a sample rate under <see cref="MinSampleRateHz"/>, a recording with no sound in it, or less than
    /// <see cref="MinDurationS"/> from the direct sound to its end.
    /// </summary>
    internal static (double[] X, int Peak) Prepare(IReadOnlyList<double> samples, int sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRateHz < MinSampleRateHz)
            throw new ArgumentException($"A sample rate of {sampleRateHz} Hz is too low to analyse in octave bands up to 8 kHz; use at least {MinSampleRateHz} Hz.", nameof(sampleRateHz));

        var peak = 0;
        var largest = 0.0;
        for (var i = 0; i < samples.Count; i++)
        {
            var magnitude = Math.Abs(samples[i]);
            if (magnitude > largest) { largest = magnitude; peak = i; }
        }
        if (!(largest > 0)) throw new ArgumentException("The recording has no sound in it.", nameof(samples));

        var length = (int)Math.Min(samples.Count - peak, MaxDurationS * sampleRateHz);
        if (length < MinDurationS * sampleRateHz)
            throw new ArgumentException($"Only {(samples.Count - peak) / (double)sampleRateHz:0.00} s of the recording follows its loudest sample; at least {MinDurationS} s is needed to see a decay.", nameof(samples));

        var x = new double[length];
        for (var i = 0; i < length; i++) x[i] = samples[peak + i];
        return (x, peak);
    }

    /// <summary>The spectrum with each band that has less than <paramref name="neededDb"/> of dynamic range made NaN.</summary>
    internal static OctaveSpectrum Needing(OctaveSpectrum spectrum, double[] dynamicRange, double neededDb) =>
        new(spectrum.Bands.Select((v, b) => dynamicRange[b] >= neededDb ? v : double.NaN).ToArray());

    /// <summary>The signal cut to each octave band, by keeping the band's part of the spectrum.</summary>
    internal static double[][] BandSignals(double[] x, int sampleRateHz)
    {
        var spectrum = Spectrum(x, out var size);
        var result = new double[8][];
        for (var b = 0; b < 8; b++)
            result[b] = FilterBand(spectrum, size, x.Length, sampleRateHz, OctaveSpectrum.Frequencies[b] / Math.Sqrt(2), OctaveSpectrum.Frequencies[b] * Math.Sqrt(2));
        return result;
    }

    /// <summary>The spectrum of the signal, zero-padded to a power of two at least twice its length so that a filtered response can ring out without wrapping round.</summary>
    internal static Complex[] Spectrum(double[] x, out int size)
    {
        size = 1;
        while (size < 2 * x.Length) size *= 2;
        var spectrum = new Complex[size];
        for (var i = 0; i < x.Length; i++) spectrum[i] = x[i];
        Fourier.Forward(spectrum, FourierOptions.Matlab);
        return spectrum;
    }

    /// <summary>The first <paramref name="length"/> samples of the signal whose <paramref name="spectrum"/> is given, cut to the band between the two edge frequencies.</summary>
    internal static double[] FilterBand(Complex[] spectrum, int size, int length, int sampleRateHz, double low, double high)
    {
        var band = new Complex[size];
        for (var k = 0; k < size; k++)
        {
            var frequency = Math.Abs((k <= size / 2 ? k : k - size) * (double)sampleRateHz / size);
            band[k] = spectrum[k] * BandGain(frequency, low, high);
        }
        Fourier.Inverse(band, FourierOptions.Matlab);
        var result = new double[length];
        for (var i = 0; i < length; i++) result[i] = band[i].Real;
        return result;
    }

    /// <summary>
    /// The filter's gain at a frequency: a Butterworth high-pass at <paramref name="low"/> times a Butterworth low-pass at
    /// <paramref name="high"/>, each of order <see cref="FilterOrder"/>, so 3 dB down at the edges and falling away smoothly outside them.
    /// </summary>
    private static double BandGain(double frequency, double low, double high)
    {
        if (frequency <= 0) return 0;
        var up = Math.Pow(frequency / low, 2 * FilterOrder);
        var down = Math.Pow(frequency / high, 2 * FilterOrder);
        return Math.Sqrt(up / (1 + up)) / Math.Sqrt(1 + down);
    }

    /// <summary>One band's energy in 1 ms bins with the noise floor taken off and the noise-dominated end cut away, and its dynamic range.</summary>
    internal static (double[] Energy, double DynamicRangeDb) Decay(double[] signal, int sampleRateHz, int centreHz)
    {
        var binCount = (int)((long)signal.Length * 1000 / sampleRateHz) + 1;
        var bins = new double[binCount];
        for (var i = 0; i < signal.Length; i++)
            bins[(int)((long)i * 1000 / sampleRateHz)] += signal[i] * signal[i];

        var tail = Math.Max(1, binCount / 10);
        var noise = 0.0;
        for (var i = binCount - tail; i < binCount; i++) noise += bins[i];
        noise /= tail;

        // the response averaged over 11 ms or four periods of the band, whichever is longer, to find the loudest part and where the noise takes over
        var half = Math.Max(SmoothingBins, (int)Math.Round(SmoothingPeriods * 1000.0 / centreHz)) / 2;
        var running = new double[binCount + 1];
        for (var i = 0; i < binCount; i++) running[i + 1] = running[i] + bins[i];
        var smooth = new double[binCount];
        var top = 0;
        for (var i = 0; i < binCount; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(binCount, i + half + 1);
            smooth[i] = (running[to] - running[from]) / (to - from);
            if (smooth[i] > smooth[top]) top = i;
        }

        var range = noise > 0 ? 10 * Math.Log10(smooth[top] / noise) : double.PositiveInfinity;
        if (!(range >= UnusableBelowDb)) return (new double[binCount], 0);

        var cut = binCount;
        for (var i = top + 1; i < binCount; i++)
            if (smooth[i] <= 2 * noise) { cut = i; break; }

        var energy = new double[binCount];
        for (var i = 0; i < cut; i++) energy[i] = Math.Max(0, bins[i] - noise);
        return (energy, range);
    }
}
