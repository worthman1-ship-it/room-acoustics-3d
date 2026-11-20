namespace RoomAcoustics.Core;

/// <summary>
/// The room's acoustics from the standard statistical equations alone, in no time: no rays and no image sources. The sound is taken as a direct
/// sound plus a diffuse reverberant field that decays exponentially (60 dB in the reverberation time T), as the Sabine and Eyring formulas assume.
/// With energies relative to a source in free space at 10 m (ISO 3382-1's strength G), the direct energy at distance r is 100 Q / r^2 and the
/// reverberant energy 31200 T / V, so
/// <list type="bullet">
/// <item>critical distance is where the two are equal: sqrt(Q V / (312 T));</item>
/// <item>G = 10 lg(100 Q / r^2 + 31200 T / V);</item>
/// <item>the reverberant part, from the arrival of the direct sound, has energy density (13.82 / T) e^(-13.82 t / T), so the share of the energy in the first
/// t seconds (D50 for 0.05 s, and the C50 and C80 clarities) and the centre time (the direct sound at 0, the reverberant part's mean at T / 13.82) follow.</item>
/// </list>
/// Eyring's time (the one for a room that is not very dead, including air absorption) is used for the decay; it is also what T30, T20 and EDT are
/// taken as, since a diffuse field has one decay rate. It cannot see the shape of the room, a source's pattern, or a receiver's place beyond its
/// distance from the source, which is what the full simulation is for.
/// </summary>
public static class StatisticalEstimate
{
    private const double DecayConstant = 13.82; // ln(10^6): a decay of 60 dB
    private const double StrengthConstant = 31200; // ISO 3382-1's 100 * 4 * 4pi / 0.161

    /// <summary>The modulation transfer at <paramref name="fm"/> Hz of a direct sound (energy <paramref name="direct"/>) plus a reverberant decay (energy <paramref name="reverberant"/>, 60 dB in <paramref name="t60S"/>).</summary>
    public static double ModulationTransfer(double fm, double direct, double reverberant, double t60S)
    {
        var wt = 2 * Math.PI * fm * t60S / DecayConstant;
        var re = direct + reverberant / (1 + wt * wt);
        var im = reverberant * wt / (1 + wt * wt);
        return Math.Sqrt(re * re + im * im) / (direct + reverberant);
    }

    /// <summary>The direct sound's energy at <paramref name="distanceM"/> and the reverberant energy of a room of this volume and reverberation time, as the estimate uses them.</summary>
    public static (double Direct, double Reverberant) Energies(double distanceM, double roomVolumeCubicMetres, double t60S, double directivityFactorQ = 1) =>
        (100 * directivityFactorQ / (distanceM * distanceM), StrengthConstant * t60S / roomVolumeCubicMetres);

    public static StatisticalResult Calculate(double roomVolumeCubicMetres, IReadOnlyList<Surface> surfaces, double distanceM, ReverberationOptions options, double directivityFactorQ = 1)
    {
        if (distanceM <= 0 || !double.IsFinite(distanceM)) throw new ArgumentOutOfRangeException(nameof(distanceM), "The distance from the source must be positive.");
        if (directivityFactorQ <= 0 || !double.IsFinite(directivityFactorQ)) throw new ArgumentOutOfRangeException(nameof(directivityFactorQ), "The directivity factor must be positive.");
        var direct = Enumerable.Repeat(100 * directivityFactorQ / (distanceM * distanceM), 8).ToArray();
        return Calculate(roomVolumeCubicMetres, surfaces, direct, Enumerable.Repeat(directivityFactorQ, 8).ToArray(), options);
    }

    /// <summary>
    /// The estimate for any number of sources: <paramref name="directEnergy"/> is the direct sound's energy in each octave band, relative (as G is) to a
    /// source of the same total power in free space at 10 m, so for sources sharing the power by s_k it is the sum of s_k 100 Q_k / r_k^2. The direct
    /// sounds are taken to arrive together, which is what the early and late windows are counted from. The critical distance is worked out for a source of
    /// directivity factor <paramref name="criticalDistanceQ"/> in each band.
    /// </summary>
    public static StatisticalResult Calculate(double roomVolumeCubicMetres, IReadOnlyList<Surface> surfaces, IReadOnlyList<double> directEnergy,
        IReadOnlyList<double> criticalDistanceQ, ReverberationOptions options)
    {
        ArgumentNullException.ThrowIfNull(directEnergy);
        ArgumentNullException.ThrowIfNull(criticalDistanceQ);
        if (directEnergy.Count != 8 || directEnergy.Any(e => !(e >= 0) || !double.IsFinite(e)))
            throw new ArgumentException("The direct energy must be eight numbers of zero or more.", nameof(directEnergy));
        if (criticalDistanceQ.Count != 8 || criticalDistanceQ.Any(q => !(q > 0) || !double.IsFinite(q)))
            throw new ArgumentException("The directivity factors must be eight positive numbers.", nameof(criticalDistanceQ));

        var sabine = ReverberationCalculator.Calculate(roomVolumeCubicMetres, surfaces, options with { Method = ReverberationMethod.Sabine });
        var eyring = ReverberationCalculator.Calculate(roomVolumeCubicMetres, surfaces, options with { Method = ReverberationMethod.EyringNorris });

        var dc = new double[8]; var d50 = new double[8]; var c50 = new double[8]; var c80 = new double[8]; var ts = new double[8]; var g = new double[8];
        var gEarly = new double[8]; var gLate = new double[8]; var mtf = new double[8][];
        for (var b = 0; b < 8; b++)
        {
            var t = eyring.T60[b];
            var direct = directEnergy[b];
            var reverberant = StrengthConstant * t / roomVolumeCubicMetres;
            double Early(double seconds) => direct + reverberant * (1 - Math.Exp(-DecayConstant * seconds / t)); // energy in the first `seconds` after the direct sound
            double Late(double seconds) => reverberant * Math.Exp(-DecayConstant * seconds / t);
            dc[b] = Math.Sqrt(criticalDistanceQ[b] * roomVolumeCubicMetres / (StrengthConstant / 100 * t));
            d50[b] = Early(0.05) / (direct + reverberant);
            c50[b] = 10 * Math.Log10(Early(0.05) / Late(0.05));
            c80[b] = 10 * Math.Log10(Early(0.08) / Late(0.08));
            ts[b] = reverberant * t / DecayConstant / (direct + reverberant);
            g[b] = 10 * Math.Log10(direct + reverberant);
            gEarly[b] = 10 * Math.Log10(Early(0.08));
            gLate[b] = 10 * Math.Log10(Late(0.08));
            // the modulation transfer of a direct sound plus a decay: |direct + reverberant / (1 + j w tau)| / (direct + reverberant), tau = T / 13.82
            mtf[b] = SpeechTransmissionIndex.Modulations.Select(fm => ModulationTransfer(fm, direct, reverberant, t)).ToArray();
        }
        return new StatisticalResult(sabine.T60, eyring.T60, new OctaveSpectrum(dc), new OctaveSpectrum(d50), new OctaveSpectrum(c50), new OctaveSpectrum(c80),
            new OctaveSpectrum(ts), new OctaveSpectrum(g), new OctaveSpectrum(gEarly), new OctaveSpectrum(gLate), mtf, eyring.MeanAlpha, eyring.TotalSurfaceAreaM2);
    }
}

/// <summary>
/// What <see cref="StatisticalEstimate"/> works out, per octave band: reverberation times, critical distance, D50, clarities (dB), centre time (s), strength G, Gearly
/// and Glate (dB), and the modulation transfer function at each of the STI's modulation frequencies (<see cref="SpeechTransmissionIndex.Modulations"/>).
/// </summary>
public sealed record StatisticalResult(
    OctaveSpectrum SabineT60S, OctaveSpectrum EyringT60S, OctaveSpectrum CriticalDistanceM, OctaveSpectrum D50, OctaveSpectrum C50Db, OctaveSpectrum C80Db,
    OctaveSpectrum CentreTimeS, OctaveSpectrum StrengthGDb, OctaveSpectrum EarlyStrengthGDb, OctaveSpectrum LateStrengthGDb, double[][] ModulationTransfer,
    OctaveSpectrum MeanAlpha, double TotalSurfaceAreaM2);
