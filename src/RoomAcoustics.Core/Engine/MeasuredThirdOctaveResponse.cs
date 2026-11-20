namespace RoomAcoustics.Core;

/// <summary>
/// The parameters of a measured impulse response in one-third-octave bands. Every array has one entry per band in <see cref="CentresHz"/>; a value
/// is NaN where the band could not give it.
/// </summary>
/// <param name="CentresHz">The nominal centre frequency of each band analysed, from 50 Hz up to the highest band that fits under the Nyquist frequency.</param>
/// <param name="DynamicRangeDb">Per band, the level of the response's peak above the noise floor at its end; infinity if the end is silent, zero if the band could not be used.</param>
public sealed record MeasuredThirdOctaveAnalysis(
    int[] CentresHz,
    double[] DynamicRangeDb,
    double[] EarlyDecayTimeS,
    double[] T20S,
    double[] T30S,
    double[] C50Db,
    double[] C80Db,
    double[] D50,
    double[] CentreTimeS,
    double DirectArrivalS,
    double DurationS,
    int SampleRateHz);

/// <summary>
/// The reverberation time, early decay time, clarity, definition and centre time of a measured impulse response in one-third-octave bands, the
/// bands ISO 3382-2 asks for, by the same method as the octave-band analysis of <see cref="MeasuredImpulseResponse"/>: time zero at the largest
/// sample, a zero-phase filter with Butterworth-shaped edges, the noise floor taken from the last tenth, and a decay time only where the band's
/// dynamic range exceeds its evaluation range by 10 dB.
/// </summary>
/// <remarks>
/// <para>
/// The bands are the 23 nominal ones from 50 Hz to 8 kHz (exact centres 1000·10^(n/10) Hz, edges a factor 10^(1/20) either side); those whose upper
/// edge reaches the Nyquist frequency are left out. The filters are the same order as the octave ones, so the skirts are as steep in decibels per
/// octave but the bands are narrower, and a low band rings for longer: below about 100 Hz in a short, well-damped room the band's own ringing can
/// set the decay, which is a limit of narrow-band analysis and not of the room.
/// </para>
/// <para>
/// The parameters are those of <see cref="RoomAcousticMetrics"/>, worked out eight bands at a time by the same calculator (it does not depend on
/// the band's frequency). G and the lateral measures need a calibrated or a directional recording and are not given.
/// </para>
/// </remarks>
public static class MeasuredThirdOctaveResponse
{
    /// <summary>The nominal centres of the one-third-octave bands analysed, in Hz.</summary>
    public static IReadOnlyList<int> NominalCentresHz { get; } =
        new[] { 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };

    /// <summary>The exact (base-10) centre of the nth band from 1 kHz: 1000·10^(n/10).</summary>
    internal static double ExactCentreHz(int nominalHz)
    {
        var n = (int)Math.Round(10 * Math.Log10(nominalHz / 1000.0));
        return 1000 * Math.Pow(10, n / 10.0);
    }

    /// <summary>
    /// The third-octave-band parameters of a mono impulse response. Throws <see cref="ArgumentException"/> in the cases that
    /// <see cref="MeasuredImpulseResponse.Analyse"/> does.
    /// </summary>
    public static MeasuredThirdOctaveAnalysis Analyse(IReadOnlyList<double> samples, int sampleRateHz)
    {
        var (x, peak) = MeasuredImpulseResponse.Prepare(samples, sampleRateHz);
        var spectrum = MeasuredImpulseResponse.Spectrum(x, out var size);
        var nyquist = sampleRateHz / 2.0;
        var half = Math.Pow(10, 0.05);

        var centres = new List<int>();
        var energy = new List<double[]>();
        var range = new List<double>();
        foreach (var nominal in NominalCentresHz)
        {
            var exact = ExactCentreHz(nominal);
            var high = exact * half;
            if (high >= nyquist) break;
            var band = MeasuredImpulseResponse.FilterBand(spectrum, size, x.Length, sampleRateHz, exact / half, high);
            var (e, r) = MeasuredImpulseResponse.Decay(band, sampleRateHz, (int)Math.Round(exact));
            centres.Add(nominal);
            energy.Add(e);
            range.Add(r);
        }

        var count = centres.Count;
        var edt = new double[count]; var t20 = new double[count]; var t30 = new double[count];
        var c50 = new double[count]; var c80 = new double[count]; var d50 = new double[count]; var ts = new double[count];
        for (var start = 0; start < count; start += 8)
        {
            // the calculator takes eight bands: fill the unused ones with silence
            var chunk = new double[8][];
            for (var b = 0; b < 8; b++) chunk[b] = start + b < count ? energy[start + b] : new double[energy[0].Length];
            var metrics = RoomAcousticsCalculator.Calculate(new ImpulseResponse(MeasuredImpulseResponse.BinWidthS, chunk, OctaveSpectrum.Flat(0)));
            for (var b = 0; b < 8 && start + b < count; b++)
            {
                var i = start + b;
                var dynamic = range[i];
                edt[i] = dynamic >= 10 + MeasuredImpulseResponse.DecayMarginDb ? metrics.EarlyDecayTimeS[b] : double.NaN;
                t20[i] = dynamic >= 25 + MeasuredImpulseResponse.DecayMarginDb ? metrics.T20S[b] : double.NaN;
                t30[i] = dynamic >= 35 + MeasuredImpulseResponse.DecayMarginDb ? metrics.T30S[b] : double.NaN;
                c50[i] = metrics.C50Db[b];
                c80[i] = metrics.C80Db[b];
                d50[i] = metrics.D50[b];
                ts[i] = metrics.CentreTimeS[b];
            }
        }
        return new MeasuredThirdOctaveAnalysis(centres.ToArray(), range.ToArray(), edt, t20, t30, c50, c80, d50, ts,
            peak / (double)sampleRateHz, x.Length / (double)sampleRateHz, sampleRateHz);
    }
}
