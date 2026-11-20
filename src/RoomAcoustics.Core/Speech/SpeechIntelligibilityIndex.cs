namespace RoomAcoustics.Core;

/// <summary>
/// The Speech Intelligibility Index (ANSI S3.5-1997), by its octave band procedure: how much of the speech's information reaches the listener, 0 to 1, as
/// the sum over the octave bands 250 Hz to 8 kHz of each band's importance times its audibility.
/// </summary>
/// <remarks>
/// <para>
/// A band's audibility is how far the speech stands above what disturbs it (the noise, or the ear's own threshold), over a 30 dB range from 15 dB under
/// it to 15 dB over, less a little for speech much louder than normal (clause 4). The octave procedure takes no account of the spread of masking from
/// one band to the next.
/// </para>
/// <para>
/// In a room, the speech and noise the index is worked out from are its clause 5.2 equivalents, from the modulation transfer at the listener as the STI
/// measures it: at each of nine modulation frequencies, 0.5 to 16 Hz, the modulation left after the room and the noise gives an apparent speech-to-noise
/// ratio (held to ±15 dB), and their mean, with the speech and noise together, gives the equivalent speech and noise spectrum levels. So reverberation
/// counts as noise, as it does in the STI. The listeners are taken to have normal hearing (an equivalent hearing threshold of 0 dB).
/// </para>
/// </remarks>
public static class SpeechIntelligibilityIndex
{
    /// <summary>The octave bands' centre frequencies, Hz.</summary>
    public static IReadOnlyList<int> Frequencies { get; } = new[] { 250, 500, 1000, 2000, 4000, 8000 };

    // ANSI S3.5-1997 table 4: band importance, the standard speech spectrum level at normal vocal effort, and the reference internal noise spectrum level
    private static readonly double[] Importance = { 0.0617, 0.1671, 0.2373, 0.2648, 0.2142, 0.0549 };
    private static readonly double[] NormalSpeech = { 34.75, 34.27, 25.01, 17.32, 9.33, 1.13 };
    private static readonly double[] InternalNoise = { -3.9, -9.7, -12.5, -17.7, -25.9, -7.1 };

    /// <summary>The modulation frequencies of clause 5.2, Hz.</summary>
    public static IReadOnlyList<double> Modulations { get; } = new[] { 0.5, 1, 1.5, 2, 3, 4, 6, 8, 16 };

    /// <summary>What turns an octave band's level into its spectrum level (the level in 1 Hz): 10 lg of the band's width, f (√2 - 1/√2), dB.</summary>
    public static double BandwidthDb(int band) => 10 * Math.Log10(Frequencies[band] * (Math.Sqrt(2) - 1 / Math.Sqrt(2)));

    /// <summary>
    /// The index from the equivalent speech, noise and hearing threshold spectrum levels in the six octave bands 250 Hz to 8 kHz, dB (clause 4, octave band
    /// procedure). With no threshold given, 0 dB (normal hearing).
    /// </summary>
    public static double Calculate(IReadOnlyList<double> speechSpectrumDb, IReadOnlyList<double> noiseSpectrumDb, IReadOnlyList<double>? thresholdDb = null)
    {
        ArgumentNullException.ThrowIfNull(speechSpectrumDb);
        ArgumentNullException.ThrowIfNull(noiseSpectrumDb);
        if (speechSpectrumDb.Count != 6 || noiseSpectrumDb.Count != 6 || thresholdDb is { Count: not 6 })
            throw new ArgumentException("The SII's octave band procedure takes six bands, 250 Hz to 8 kHz.");
        var index = 0.0;
        for (var i = 0; i < 6; i++)
        {
            var speech = speechSpectrumDb[i];
            var masking = noiseSpectrumDb[i];                                         // step 3: the octave procedure has no spread of masking
            var internalNoise = InternalNoise[i] + (thresholdDb?[i] ?? 0);           // step 4
            var disturbance = Math.Max(masking, internalNoise);                       // step 5
            var distortion = Math.Min(1, 1 - (speech - NormalSpeech[i] - 10) / 160); // step 6
            var k = Math.Clamp((speech - disturbance + 15) / 30, 0, 1);               // step 7
            index += Importance[i] * distortion * k;                                  // step 8
        }
        return index;
    }

    /// <summary>
    /// The index at a listener in a room (clause 5.2): <paramref name="speech"/> and <paramref name="noise"/> are the octave band levels there (63 Hz to 8 kHz,
    /// as the engine's bands), and <paramref name="modulationTransfer"/> the room's modulation transfer, taking the band (0 for 63 Hz to 7 for 8 kHz) and any
    /// modulation frequency in Hz.
    /// </summary>
    public static double InRoom(OctaveSpectrum speech, OctaveSpectrum noise, Func<int, double, double> modulationTransfer)
    {
        ArgumentNullException.ThrowIfNull(modulationTransfer);
        var equivalentSpeech = new double[6];
        var equivalentNoise = new double[6];
        for (var i = 0; i < 6; i++)
        {
            var band = i + 2; // 250 Hz is the engine's third band
            double s = Math.Pow(10, speech[band] / 10), n = Math.Pow(10, noise[band] / 10);
            // the apparent speech-to-noise ratio: the modulation the room leaves, reduced again by the noise, at each modulation frequency (equation 22)
            var ratio = Modulations.Average(fm =>
            {
                var m = modulationTransfer(band, fm) * s / (s + n);
                return m >= 1 ? 15 : m <= 0 ? -15 : Math.Clamp(10 * Math.Log10(m / (1 - m)), -15, 15);
            });
            // the speech and noise together, as a spectrum level, shared out by that ratio (equations 23 and 24)
            var combined = 10 * Math.Log10(s + n) - BandwidthDb(i);
            equivalentSpeech[i] = ratio + combined - 10 * Math.Log10(1 + Math.Pow(10, ratio / 10));
            equivalentNoise[i] = equivalentSpeech[i] - ratio;
        }
        return Calculate(equivalentSpeech, equivalentNoise);
    }
}
