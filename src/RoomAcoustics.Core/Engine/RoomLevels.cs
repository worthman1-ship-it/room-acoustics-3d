using Sti = RoomAcoustics.Core.SpeechTransmissionIndex;
using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>
/// Absolute levels and speech intelligibility at a receiver, from the strength G and the impulse response of
/// <see cref="RoomAcousticsCalculator"/> and a source's sound power spectrum.
/// </summary>
/// <remarks>
/// G is the level at the receiver relative to the free-field level at 10 m from the same source, and a source of
/// sound power level Lw gives 10 m free-field level Lw - 31 dB (Lw - 20 lg 10 - 11), so Lp = Lw + G - 31 in each band.
/// This is the direct plus reverberant field of the simulated room, with air absorption; there is no background noise
/// in it.
/// </remarks>
public static class RoomLevels
{
    /// <summary>Free-field sound pressure level at 10 m for a source of sound power level Lw: Lw - 20 lg(10) - 11.</summary>
    public const double FreeFieldLevelOffsetAt10mDb = 31;

    /// <summary>Octave-band sound pressure level at the receiver for a source of the given sound power spectrum.</summary>
    public static OctaveSpectrum SoundPressureLevels(RoomAcousticMetrics metrics, OctaveSpectrum sourcePowerLevelDb)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new OctaveSpectrum(Enumerable.Range(0, 8)
            .Select(b => sourcePowerLevelDb[b] + metrics.StrengthGDb[b] - FreeFieldLevelOffsetAt10mDb).ToArray());
    }

    /// <summary>
    /// Speech Transmission Index at the receiver, from the room's own modulation transfer function (the Fourier transform
    /// of the squared impulse response, IEC 60268-16, Schroeder), the speech spectrum and a background noise spectrum.
    /// </summary>
    /// <param name="ir">The receiver's impulse response.</param>
    /// <param name="speechPowerLevelDb">Octave-band sound power level of the talker (or loudspeaker).</param>
    /// <param name="backgroundNoiseDb">Octave-band background noise sound pressure level at the receiver.</param>
    public static double SpeechTransmissionIndex(ImpulseResponse ir, OctaveSpectrum speechPowerLevelDb, OctaveSpectrum backgroundNoiseDb,
        SpeechWeighting weighting = SpeechWeighting.Male)
    {
        ArgumentNullException.ThrowIfNull(ir);
        return SpeechTransmissionIndex(RoomAcousticsCalculator.Calculate(ir), speechPowerLevelDb, backgroundNoiseDb, weighting);
    }

    /// <summary>The same, from the metrics already worked out for a receiver (which carry the modulation transfer function).</summary>
    public static double SpeechTransmissionIndex(RoomAcousticMetrics metrics, OctaveSpectrum speechPowerLevelDb, OctaveSpectrum backgroundNoiseDb,
        SpeechWeighting weighting = SpeechWeighting.Male)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var speech = SoundPressureLevels(metrics, speechPowerLevelDb);
        var modulations = Sti.Modulations.Count;
        return Sti.Calculate(speech, backgroundNoiseDb,
            (band, fm) => metrics.ModulationTransfer[band][ModulationIndex(fm, modulations)], weighting);
    }

    private static int ModulationIndex(double fm, int count)
    {
        var list = Sti.Modulations;
        for (var i = 0; i < count; i++)
            if (list[i] == fm) return i;
        throw new ArgumentOutOfRangeException(nameof(fm), fm, "Not one of the STI modulation frequencies.");
    }

    /// <summary>
    /// The modulation transfer function of an energy impulse response at modulation frequency <paramref name="fm"/>:
    /// |sum h(t) e^(-j 2 pi fm t)| / sum h(t). 1 for a single impulse; falls towards 0 as the energy is spread in time.
    /// </summary>
    public static double ModulationTransfer(double[] energy, double binWidthS, double fm)
    {
        double re = 0, im = 0, total = 0;
        for (var i = 0; i < energy.Length; i++)
        {
            var angle = 2 * Math.PI * fm * (i + 0.5) * binWidthS;
            re += energy[i] * Math.Cos(angle);
            im -= energy[i] * Math.Sin(angle);
            total += energy[i];
        }
        return total > 0 ? Math.Sqrt(re * re + im * im) / total : double.NaN;
    }
}
