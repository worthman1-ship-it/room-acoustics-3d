using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>
/// Room-acoustic parameters per octave band, as defined in ISO 3382-1.
/// Units: EDT, T20, T30 and centre time in seconds; C50 and C80 in dB;
/// D50 as a fraction (0-1, multiply by 100 for the percentage ISO quotes);
/// G, Gearly (0 to 80 ms), Glate (after 80 ms) and LJ in dB; LF80 and LFC80 as
/// fractions. Undefined values are NaN. The lateral measures (LF80, LFC80, LJ) are
/// NaN when the impulse response carries no arrival directions. <c>ModulationTransfer</c> is the room's modulation
/// transfer function in each octave band at the 14 STI modulation frequencies (0.63 to 12.5 Hz), kept so that the
/// Speech Transmission Index can be worked out for any speech and noise level without the impulse response.
/// <c>EchoCriterionSpeech</c> and <c>EchoCriterionMusic</c> are the largest Dietsch and Kraak echo criterion
/// (<see cref="EchoCriterion"/>) in each band, NaN where the response is too short to show one.
/// <c>StageSupportEarlyDb</c>, <c>StageSupportLateDb</c> and <c>StageSupportTotalDb</c> are Gade's support ST_early, ST_late and ST_total in dB, timed from the direct
/// sound, and <c>EarlyEnsembleLevelDb</c> his early ensemble level EEL.
/// <c>DirectStrengthGDb</c> is the strength of the direct sound alone (G of it, so 20 lg(10 / r) for an omnidirectional source in free air) and
/// <c>DirectToReverberantDb</c> is the direct sound against everything after it (every reflection, early and late), in dB; both NaN where a wall blocks
/// the direct sound, and null for a response that does not know its direct sound (a measured one).
/// <c>IaccEarly</c>, <c>IaccLate</c> and <c>IaccAll</c> are the interaural cross-correlation coefficient of a head facing the source over 0 to 80 ms, 80 ms to 1 s
/// and 0 to 1 s after the first arrival (<see cref="InterauralCoherence"/>); null for a response traced without <see cref="RayTraceOptions.Interaural"/>.
/// </summary>
public sealed record RoomAcousticMetrics(
    OctaveSpectrum EarlyDecayTimeS,
    OctaveSpectrum T20S,
    OctaveSpectrum T30S,
    OctaveSpectrum C50Db,
    OctaveSpectrum C80Db,
    OctaveSpectrum D50,
    OctaveSpectrum CentreTimeS,
    OctaveSpectrum StrengthGDb,
    OctaveSpectrum EarlyStrengthGDb,
    OctaveSpectrum LateStrengthGDb,
    OctaveSpectrum LateralFraction80,
    OctaveSpectrum LateralFractionCoefficient80,
    OctaveSpectrum LateLateralLevelLJDb,
    OctaveSpectrum EchoCriterionSpeech,
    OctaveSpectrum EchoCriterionMusic,
    OctaveSpectrum StageSupportEarlyDb,
    OctaveSpectrum StageSupportLateDb,
    IReadOnlyList<double[]> ModulationTransfer,
    OctaveSpectrum? DirectStrengthGDb = null,
    OctaveSpectrum? DirectToReverberantDb = null,
    OctaveSpectrum? StageSupportTotalDb = null,
    OctaveSpectrum? EarlyEnsembleLevelDb = null,
    OctaveSpectrum? IaccEarly = null,
    OctaveSpectrum? IaccLate = null,
    OctaveSpectrum? IaccAll = null);

/// <summary>
/// Computes the ISO 3382-1 parameters from an energy impulse response.
/// Reverberation times come from the Schroeder backward-integrated decay
/// curve, by linear regression over the standard decay ranges: EDT over
/// 0 to -10 dB, T20 over -5 to -25 dB, T30 over -5 to -35 dB, each
/// extrapolated to a 60 dB decay. As the standard says, times run from the arrival of the direct sound (each band's first bin with energy):
/// the 50 and 80 ms of C50, C80, D50, LF80 and LJ, the centre time, and the decay curve, which would otherwise begin with a flat stretch
/// at 0 dB, before any sound arrives, that pulls the EDT's fit shallower.
/// </summary>
/// <remarks>
/// <para>
/// Gearly and Glate are G taken over the first 80 ms and after it, so that Gearly - Glate = C80 and the two add (as
/// energy) to G. The lateral fraction LF80 is the energy in 5 to 80 ms weighted by cos² of the angle to the receiver's
/// lateral axis, over the energy in 0 to 80 ms (ISO 3382-1 uses a figure-of-eight microphone for the numerator).
/// LFC80 weights by |cos| instead. LJ is the energy after 80 ms weighted by cos², in dB against the free-field energy
/// at 10 m. The lateral axis is horizontal and perpendicular to the line to the source.
/// </para>
/// <para>
/// Stage support (Gade, 1989) is for a receiver about 1 m from the source, where the direct sound dominates: ST_early is the energy from 20 to
/// 100 ms over that in the first 10 ms, ST_late the energy from 100 ms to 1 s over the same, in dB. The times run from the arrival of the
/// direct sound, taken as the first bin with any energy in the band, so the first 10 ms hold the direct sound and whatever reflections follow
/// within 10 ms of it (the floor's, at 1 m). ST_total is the energy from 20 ms to 1 s over the same. Gade quotes them for a performer hearing the
/// others and themselves on a platform.
/// </para>
/// <para>
/// The early ensemble level (Gade, 1989) is for a musician hearing another across the platform: the energy in the first 80 ms at the receiver over the
/// energy of the first 10 ms at 1 m from the source, in dB. Here that reference is the free-field direct sound at 1 m, 100 times the free-field energy
/// at 10 m that G is taken against, so EEL is G_early less 20 dB. Measured on a platform, the reference also holds the floor's reflection, which
/// would put EEL up to 3 dB lower.
/// </para>
/// </remarks>
public static class RoomAcousticsCalculator
{
    public static RoomAcousticMetrics Calculate(ImpulseResponse ir)
    {
        ArgumentNullException.ThrowIfNull(ir);
        var edt = new double[8];
        var t20 = new double[8];
        var t30 = new double[8];
        var c50 = new double[8];
        var c80 = new double[8];
        var d50 = new double[8];
        var centre = new double[8];
        var g = new double[8];
        var gEarly = new double[8];
        var gLate = new double[8];
        var lf80 = new double[8];
        var lfc80 = new double[8];
        var lj = new double[8];
        var ekSpeech = new double[8];
        var ekMusic = new double[8];
        var stEarly = new double[8];
        var stLate = new double[8];
        var stTotal = new double[8];
        var eel = new double[8];
        var mtf = new double[8][];
        var gDirect = new double[8];
        var directToReverberant = new double[8];

        for (var b = 0; b < 8; b++)
        {
            var h = ir.Energy[b];
            var dt = ir.BinWidthS;
            // ISO 3382-1 times everything from the arrival of the direct sound, not from the moment the source sounds: the first bin with energy
            var arrival = Array.FindIndex(h, v => v > 0);
            var t0 = Math.Max(0, arrival) * dt;
            var edcDb = DecayAnalysis.SchroederDb(arrival > 0 ? h[arrival..] : h);

            edt[b] = DecayAnalysis.EarlyDecayFit(edcDb, dt)?.ReverberationTimeS ?? double.NaN;
            t20[b] = DecayAnalysis.T20Fit(edcDb, dt)?.ReverberationTimeS ?? double.NaN;
            t30[b] = DecayAnalysis.T30Fit(edcDb, dt)?.ReverberationTimeS ?? double.NaN;

            var total = h.Sum();
            var early50 = EnergyBefore(h, dt, t0 + 0.05);
            var early80 = EnergyBefore(h, dt, t0 + 0.08);
            c50[b] = Clarity(early50, total - early50);
            c80[b] = Clarity(early80, total - early80);
            d50[b] = total > 0 ? early50 / total : double.NaN;

            var weighted = 0.0;
            for (var i = 0; i < h.Length; i++)
                weighted += ((i + 0.5) * dt - t0) * h[i];
            centre[b] = total > 0 ? weighted / total : double.NaN;

            g[b] = total > 0 && ir.ReferenceEnergy10m[b] > 0
                ? 10 * Math.Log10(total / ir.ReferenceEnergy10m[b])
                : double.NaN;

            ekSpeech[b] = EchoCriterion.Evaluate(h, dt, EchoKind.Speech)?.Value ?? double.NaN;
            ekMusic[b] = EchoCriterion.Evaluate(h, dt, EchoKind.Music)?.Value ?? double.NaN;

            if (arrival >= 0)
            {
                var direct = EnergyBetween(h, dt, t0, t0 + 0.01);
                stEarly[b] = Clarity(EnergyBetween(h, dt, t0 + 0.02, t0 + 0.1), direct);
                stLate[b] = Clarity(EnergyBetween(h, dt, t0 + 0.1, t0 + 1.0), direct);
                stTotal[b] = Clarity(EnergyBetween(h, dt, t0 + 0.02, t0 + 1.0), direct);
            }
            else
            {
                stEarly[b] = stLate[b] = stTotal[b] = double.NaN;
            }

            mtf[b] = SpeechTransmissionIndex.Modulations.Select(fm => RoomLevels.ModulationTransfer(h, dt, fm)).ToArray();

            var reference = ir.ReferenceEnergy10m[b];
            gEarly[b] = StrengthDb(early80, reference);
            eel[b] = StrengthDb(early80, 100 * reference);
            gLate[b] = StrengthDb(total - early80, reference);
            if (ir.DirectEnergy is { } directEnergy)
            {
                gDirect[b] = StrengthDb(directEnergy[b], reference);
                directToReverberant[b] = directEnergy[b] > 0 && total - directEnergy[b] > 0 ? 10 * Math.Log10(directEnergy[b] / (total - directEnergy[b])) : double.NaN;
            }

            if (ir.LateralEnergy is { } lateral && ir.LateralAmplitudeEnergy is { } amplitude)
            {
                lf80[b] = Ratio(EnergyBetween(lateral[b], dt, t0 + 0.005, t0 + 0.08), early80);
                lfc80[b] = Ratio(EnergyBetween(amplitude[b], dt, t0 + 0.005, t0 + 0.08), early80);
                lj[b] = StrengthDb(EnergyBetween(lateral[b], dt, t0 + 0.08, double.PositiveInfinity), reference);
            }
            else
            {
                lf80[b] = lfc80[b] = lj[b] = double.NaN;
            }
        }

        return new RoomAcousticMetrics(
            new OctaveSpectrum(edt), new OctaveSpectrum(t20), new OctaveSpectrum(t30),
            new OctaveSpectrum(c50), new OctaveSpectrum(c80), new OctaveSpectrum(d50),
            new OctaveSpectrum(centre), new OctaveSpectrum(g),
            new OctaveSpectrum(gEarly), new OctaveSpectrum(gLate),
            new OctaveSpectrum(lf80), new OctaveSpectrum(lfc80), new OctaveSpectrum(lj),
            new OctaveSpectrum(ekSpeech), new OctaveSpectrum(ekMusic),
            new OctaveSpectrum(stEarly), new OctaveSpectrum(stLate), mtf,
            ir.DirectEnergy is null ? (OctaveSpectrum?)null : new OctaveSpectrum(gDirect), ir.DirectEnergy is null ? (OctaveSpectrum?)null : new OctaveSpectrum(directToReverberant),
            new OctaveSpectrum(stTotal), new OctaveSpectrum(eel),
            ir.Interaural is { } ia ? InterauralCoherence.Bands(ia.Early) : null, ir.Interaural is { } il ? InterauralCoherence.Bands(il.Late) : null,
            ir.Interaural is { } all ? InterauralCoherence.Bands(all.Early.Zip(all.Late, (e, l) => e.Zip(l, (x, y) => x + y).ToArray()).ToList()) : null);
    }

    private static double StrengthDb(double energy, double reference) =>
        energy > 0 && reference > 0 ? 10 * Math.Log10(energy / reference) : double.NaN;

    private static double Ratio(double numerator, double denominator) =>
        denominator > 0 ? numerator / denominator : double.NaN;

    /// <summary>Energy in the bins whose start time is at least <paramref name="fromS"/> and before <paramref name="toS"/>.</summary>
    private static double EnergyBetween(double[] h, double dt, double fromS, double toS)
    {
        var sum = 0.0;
        for (var i = 0; i < h.Length; i++)
        {
            var t = i * dt;
            if (t >= fromS - 1e-12 && t < toS - 1e-12) sum += h[i];
        }
        return sum;
    }

    private static double EnergyBefore(double[] h, double dt, double limitS)
    {
        var sum = 0.0;
        for (var i = 0; i < h.Length && i * dt < limitS - 1e-12; i++)
            sum += h[i];
        return sum;
    }

    private static double Clarity(double early, double late) =>
        early > 0 && late > 0 ? 10 * Math.Log10(early / late) : double.NaN;
}
