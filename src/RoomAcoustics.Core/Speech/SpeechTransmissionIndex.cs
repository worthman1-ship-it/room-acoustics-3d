namespace RoomAcoustics.Core;

public enum SpeechWeighting { Male, Female }

/// <summary>
/// Speech Transmission Index, ported from Richard's `STI` VBA module
/// (23 September 2014, "based on Norsonic calc sheet, based on IEC 60268-16
/// (May 2003)"; `STI_Predict` headed IEC 60268-16:2011): per octave band
/// 125 Hz - 8 kHz, the modulation transfer at 14 modulation frequencies
/// (0.63 - 12.5 Hz) from reverberation (exponential decay), background noise,
/// the absolute reception threshold and level-dependent auditory masking from
/// the octave below; effective SNR clamped to ±15 dB, mean transmission index
/// per band, then the male or female band weightings with redundancy
/// correction.
/// </summary>
/// <remarks>
/// Validated against the example workbook's cached `STI_Predict` results
/// (five background NC levels at zero RT). The auditory masking factor table
/// is reproduced exactly as written in every copy of the module: 1e-4 for
/// 45-55 dB, 3.16e-4 for 55-65 dB, 3.162e-4 again for 65-75 dB, 1e-3 for 75-85
/// dB, 3.1622e-3 for 85-95 dB and 0.1 above 95 dB. That sequence looks
/// irregular (a repeated step, then a jump); no copy of IEC 60268-16 was
/// available to check it, so it is flagged rather than changed.
/// </remarks>
public static class SpeechTransmissionIndex
{
    private static readonly double[] ModulationFrequencies = { 0.63, 0.8, 1, 1.25, 1.6, 2, 2.5, 3.15, 4, 5, 6.3, 8, 10, 12.5 };
    private static readonly double[] ReceptionThreshold = { 0, 46, 27, 12, 6.5, 7.5, 8, 12 };
    private static readonly double[] MaleAlpha = { 0.085, 0.127, 0.23, 0.233, 0.309, 0.224, 0.173 };
    private static readonly double[] MaleBeta = { 0.085, 0.078, 0.065, 0.011, 0.047, 0.095, 0 };
    private static readonly double[] FemaleAlpha = { 0, 0.117, 0.223, 0.216, 0.328, 0.25, 0.194 };
    private static readonly double[] FemaleBeta = { 0, 0.099, 0.066, 0.062, 0.025, 0.076, 0 };
    private const double Pi = 3.1415926539;

    private static double Intensity(double level) => Math.Pow(10, (level - 93.98) / 10);

    /// <summary>Auditory masking intensity from the octave below, per the source's level-dependent factor table.</summary>
    public static double MaskingIntensity(double lowerSignal, double lowerNoise)
    {
        var level = 10 * Math.Log10(Math.Pow(10, lowerSignal / 10) + Math.Pow(10, lowerNoise / 10));
        var factor = level switch
        {
            > 95 => 0.1,
            > 85 => 0.0031622,
            > 75 => 0.001,
            > 65 => 0.0003162,
            > 55 => 0.000316,
            > 45 => 0.0001,
            _ => 0,
        };
        return factor * Intensity(level);
    }

    /// <summary>Mean transmission index for one octave band (the source's `mtf`).</summary>
    /// <param name="bandIndex">1 (125 Hz) to 7 (8 kHz).</param>
    public static double TransmissionIndex(int bandIndex, double signal, double noise, double reverberationTimeS, double lowerSignal, double lowerNoise) =>
        TransmissionIndex(bandIndex, signal, noise, fm => 1 / Math.Sqrt(1 + Math.Pow(2 * Pi * fm * reverberationTimeS / 13.8, 2)),
            lowerSignal, lowerNoise);

    /// <summary>The modulation frequencies (Hz) the index is taken at: 0.63 to 12.5 Hz in 1/3-octave steps.</summary>
    public static IReadOnlyList<double> Modulations => ModulationFrequencies;

    /// <summary>
    /// Mean transmission index for one octave band from a modulation transfer function, the reduction in modulation depth
    /// at each modulation frequency caused by the room alone. The reverberation-time overload uses the exponential-decay
    /// function; a measured or simulated impulse response gives its own.
    /// </summary>
    public static double TransmissionIndex(int bandIndex, double signal, double noise, Func<double, double> modulationTransfer, double lowerSignal, double lowerNoise) =>
        TransmissionIndex(bandIndex, signal, noise, modulationTransfer, lowerSignal, lowerNoise, ModulationFrequencies);

    private static double TransmissionIndex(int bandIndex, double signal, double noise, Func<double, double> modulationTransfer, double lowerSignal, double lowerNoise, IReadOnlyList<double> frequencies)
    {
        var mask = MaskingIntensity(lowerSignal, lowerNoise);
        var total = 0.0;
        foreach (var fm in frequencies)
        {
            var m = modulationTransfer(fm);
            var correlated = m * Intensity(signal)
                / (Intensity(signal) + Intensity(noise) + Intensity(ReceptionThreshold[bandIndex]) + mask);
            var snr = Math.Clamp(10 * Math.Log10(correlated / (1 - correlated)), -15, 15);
            total += (snr + 15) / 30;
        }
        return total / frequencies.Count;
    }

    /// <summary>
    /// STI from octave-band speech and noise levels at the listener and the
    /// room's reverberation time (63 Hz - 8 kHz; 63 Hz only enters as the
    /// masker of the 125 Hz band), rounded to 0.01 as the source does.
    /// </summary>
    public static double Calculate(OctaveSpectrum speech, OctaveSpectrum noise, OctaveSpectrum reverberationTimeS, SpeechWeighting weighting = SpeechWeighting.Male) =>
        Calculate(speech, noise,
            (band, fm) => 1 / Math.Sqrt(1 + Math.Pow(2 * Pi * fm * reverberationTimeS[band] / 13.8, 2)), weighting);

    /// <summary>
    /// STI from octave-band speech and noise levels and a modulation transfer function for each octave band
    /// (<paramref name="modulationTransfer"/> takes the band index, 1 for 125 Hz to 7 for 8 kHz, and the modulation frequency in Hz).
    /// </summary>
    public static double Calculate(OctaveSpectrum speech, OctaveSpectrum noise, Func<int, double, double> modulationTransfer, SpeechWeighting weighting = SpeechWeighting.Male)
    {
        var mti = new double[9];
        for (var k = 1; k <= 7; k++)
        {
            var band = k;
            mti[k] = TransmissionIndex(k, speech[k], noise[k], fm => modulationTransfer(band, fm), speech[k - 1], noise[k - 1]);
        }

        return Weighted(mti, weighting);
    }

    // STIPA (IEC 60268-16:2011): two modulation frequencies in each octave band 125 Hz to 8 kHz, a different pair in each band
    private static readonly double[][] StipaFrequencies =
    {
        Array.Empty<double>(), new[] { 1.6, 8 }, new[] { 1.0, 5 }, new[] { 0.63, 3.15 }, new[] { 2.0, 10 }, new[] { 1.25, 6.25 }, new[] { 0.8, 4 }, new[] { 2.5, 12.5 },
    };

    // RaSTI (IEC 60268-16:1998): four modulation frequencies at 500 Hz and five at 2 kHz
    private static readonly double[] RastiFrequencies500 = { 1, 2, 4, 8 };
    private static readonly double[] RastiFrequencies2000 = { 0.7, 1.4, 2.8, 5.6, 11.2 };

    /// <summary>
    /// STIPA, the quick form of the STI: each octave band's transmission index is taken from two modulation frequencies (a different pair in each band) instead of
    /// fourteen, then weighted as the full STI is. The <paramref name="modulationTransfer"/> function takes the band index (1 for 125 Hz to 7 for 8 kHz) and any
    /// modulation frequency in Hz, so it has to be a function of the response itself, not a table at the full STI's frequencies. Rounded to 0.01.
    /// </summary>
    public static double Stipa(OctaveSpectrum speech, OctaveSpectrum noise, Func<int, double, double> modulationTransfer, SpeechWeighting weighting = SpeechWeighting.Male)
    {
        var mti = new double[9];
        for (var k = 1; k <= 7; k++)
        {
            var band = k;
            mti[k] = TransmissionIndex(k, speech[k], noise[k], fm => modulationTransfer(band, fm), speech[k - 1], noise[k - 1], StipaFrequencies[k]);
        }
        return Weighted(mti, weighting);
    }

    /// <summary>
    /// RaSTI, the older speech-intelligibility index for public-address systems: the mean of the 500 Hz band's transmission index (four modulation frequencies) and
    /// the 2 kHz band's (five), with no redundancy correction and no male or female weighting. Rounded to 0.01.
    /// </summary>
    public static double Rasti(OctaveSpectrum speech, OctaveSpectrum noise, Func<int, double, double> modulationTransfer)
    {
        double Band(int k, double[] frequencies) => TransmissionIndex(k, speech[k], noise[k], fm => modulationTransfer(k, fm), speech[k - 1], noise[k - 1], frequencies);
        return Math.Round((Band(3, RastiFrequencies500) + Band(5, RastiFrequencies2000)) / 2, 2);
    }

    private static double Weighted(double[] mti, SpeechWeighting weighting)
    {
        var (alpha, beta) = weighting == SpeechWeighting.Male ? (MaleAlpha, MaleBeta) : (FemaleAlpha, FemaleBeta);
        var sti = 0.0;
        for (var k = 1; k <= 7; k++)
            sti += alpha[k - 1] * mti[k] - Math.Sqrt(mti[k] * mti[k + 1]) * beta[k - 1];
        return Math.Round(sti, 2);
    }

    /// <summary>The speech spectrum at a receiver (the standard speech at <paramref name="speechLevelDbAAt1m"/> taken there by the room's strength G), as the STI measures use it.</summary>
    public static OctaveSpectrum SpeechAtReceiver(OctaveSpectrum strengthGDb, double speechLevelDbAAt1m)
    {
        var at1m = PredictorSpeech(speechLevelDbAAt1m);
        return new OctaveSpectrum(Enumerable.Range(0, 8).Select(b => at1m[b] - 20 + strengthGDb[b]).ToArray());
    }

    /// <summary>
    /// STI at a receiver in a room: the speech spectrum there is the standard speech spectrum at <paramref name="speechLevelDbAAt1m"/> (A-weighted, at 1 m from the
    /// talker) taken to the receiver by the room's strength G (G is against the free field at 10 m, 20 dB below 1 m), the noise is an NC curve
    /// (<paramref name="backgroundNc"/> 15 to 70 in steps of 5; any other value is no noise), and the modulation transfer is the room's own at each octave band.
    /// </summary>
    public static double AtReceiver(OctaveSpectrum strengthGDb, Func<int, double, double> modulationTransfer, double speechLevelDbAAt1m, int backgroundNc, SpeechWeighting weighting = SpeechWeighting.Male)
    {
        return Calculate(SpeechAtReceiver(strengthGDb, speechLevelDbAAt1m), NoiseCriterion.Curve(backgroundNc), modulationTransfer, weighting);
    }

    private static readonly double[] ReferenceSpeech = { 0, 49.5, 55.5, 59.6, 56.3, 50.8, 45.3, 36.6 };
    private static readonly double[] RtMultipliers = { 1.5, 1.2, 1, 1, 1, 1, 1, 0.9 };

    /// <summary>
    /// The 2017 quick predictor (`STI_Predict(Rev_time, Background_NC, speech_level)`):
    /// a reference speech spectrum shifted by (speech level - 61 dB), a background at
    /// an NC curve, and the RT scaled x1.5/x1.2/.../x0.9 across the bands; male weighting.
    /// </summary>
    public static double Predict(double reverberationTimeS, int backgroundNc, double speechLevelDbA) =>
        Calculate(PredictorSpeech(speechLevelDbA), NoiseCriterion.Curve(backgroundNc), PredictorReverberationTimes(reverberationTimeS));

    /// <summary>The predictor's speech spectrum: the reference spectrum shifted by (speech level - 61 dB).</summary>
    public static OctaveSpectrum PredictorSpeech(double speechLevelDbA) =>
        new(ReferenceSpeech.Select(v => v + speechLevelDbA - 61).ToArray());

    /// <summary>The predictor's RT spectrum: x1.5 at 63 Hz, x1.2 at 125 Hz, x0.9 at 8 kHz.</summary>
    public static OctaveSpectrum PredictorReverberationTimes(double reverberationTimeS) =>
        new(RtMultipliers.Select(f => f * reverberationTimeS).ToArray());

    /// <summary>The articulation loss of consonants in percent, %ALcons = 170.5405 e^(-5.419 STI) (IEC 60268-16), held to 0 to 100.</summary>
    public static double Alcons(double sti) => double.IsFinite(sti) ? Math.Clamp(170.5405 * Math.Exp(-5.419 * sti), 0, 100) : double.NaN;

    /// <summary>A qualitative rating for an STI value (the commonly used IEC 60268-16 bands).</summary>
    public static string Rating(double sti) => sti switch
    {
        < 0.3 => "Bad",
        < 0.45 => "Poor",
        < 0.6 => "Fair",
        < 0.75 => "Good",
        _ => "Excellent",
    };
}

/// <summary>NC curves NC15 - NC70 (63 Hz - 8 kHz), as the STI module's `NC` table ("taken from Little Red Book").</summary>
public static class NoiseCriterion
{
    private static readonly Dictionary<int, double[]> Curves = new()
    {
        [15] = new[] { 47.5, 36, 29, 22, 17, 14, 12, 11 },
        [20] = new[] { 50.5, 40, 33, 26.5, 22, 19, 17, 16 },
        [25] = new[] { 54, 44.5, 37.5, 31, 27, 24, 22, 21 },
        [30] = new[] { 57, 48, 41, 35.5, 31.5, 29, 28, 27 },
        [35] = new[] { 60.5, 52.5, 45, 40, 36.5, 34.5, 33, 32 },
        [40] = new[] { 64, 56, 50, 45, 41, 39.5, 38.5, 37 },
        [45] = new[] { 67, 60, 54, 49, 46.5, 44.5, 43, 42 },
        [50] = new[] { 71, 64, 58.5, 54, 51, 49.5, 47.5, 47 },
        [55] = new[] { 74, 67.5, 62.5, 58.5, 56, 54, 52.5, 51.5 },
        [60] = new[] { 77, 71.5, 67, 63.5, 61.5, 59.5, 58, 57 },
        [65] = new[] { 80, 75, 71, 68.5, 66, 64, 63, 62 },
        [70] = new[] { 83.5, 78.5, 75, 72, 70.5, 70, 69, 68 },
    };

    public static IReadOnlyList<int> Ratings { get; } = Curves.Keys.ToList();

    /// <summary>The curve for NC15 - NC70 in steps of 5; any other value returns 0 dB in every band, as the source does.</summary>
    public static OctaveSpectrum Curve(int nc) => Curves.TryGetValue(nc, out var c) ? new OctaveSpectrum(c) : OctaveSpectrum.Flat(0);
}
