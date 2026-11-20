using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>The simulated room-acoustic parameters of one one-third-octave band at one receiver.</summary>
/// <param name="NominalHz">The nominal centre frequency, 50 to 8000 Hz.</param>
/// <param name="ExactHz">The exact (base-10) centre frequency, 1000·10^(n/10).</param>
/// <param name="EdtS">Early decay time, s.</param>
/// <param name="T20S">T20, s.</param>
/// <param name="T30S">T30, s.</param>
/// <param name="C50Db">Clarity C50, dB.</param>
/// <param name="C80Db">Clarity C80, dB.</param>
/// <param name="D50">Definition D50, a fraction.</param>
/// <param name="CentreTimeS">Centre time Ts, s.</param>
/// <param name="StrengthGDb">Strength G, dB.</param>
public sealed record SimulatedThirdOctaveBand(int NominalHz, double ExactHz, double EdtS, double T20S, double T30S,
    double C50Db, double C80Db, double D50, double CentreTimeS, double StrengthGDb);

/// <summary>
/// The simulation in one-third-octave bands, 50 Hz to 8 kHz (23 bands), at one receiver. The ray and image-source tracer works eight bands at
/// a time, with a surface's absorption and scattering and the source's pattern given per band, so the 23 bands are run as three passes of eight:
/// in each, the room is rebuilt with every surface's absorption and scattering at the centres of those eight bands, the source's pattern for each
/// is that of the octave band the centre lies in, and air absorption is taken at the centre itself. The rays are the same in each pass (the same
/// seed), so the third-octave bands differ only through what the frequency changes.
/// </summary>
/// <remarks>
/// A surface's absorption and scattering are known in octave bands only, so the value in a third-octave band is found by interpolating the octave
/// values linearly against the logarithm of the frequency, held at the 63 Hz value below it and the 8 kHz value above it. That is a smooth guess,
/// not data: a material with a sharp resonance (a panel absorber) is spread out, and the 50 Hz and 80 Hz bands are the 63 Hz value or between it and
/// 125 Hz. Use measured third-octave absorption by entering it as the octave values if it is all you have.
/// </remarks>
public static class ThirdOctaveSimulation
{
    /// <summary>The octave-band values interpolated to a frequency, linearly in the logarithm of the frequency, held at the ends.</summary>
    public static double Interpolate(IReadOnlyList<double> octaveValues, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(octaveValues);
        if (octaveValues.Count != 8) throw new ArgumentException("There must be eight octave-band values.", nameof(octaveValues));
        if (!(frequencyHz > 0) || !double.IsFinite(frequencyHz)) throw new ArgumentOutOfRangeException(nameof(frequencyHz), "The frequency must be positive.");
        var centres = OctaveSpectrum.Frequencies;
        if (frequencyHz <= centres[0]) return octaveValues[0];
        if (frequencyHz >= centres[7]) return octaveValues[7];
        var upper = 1;
        while (centres[upper] < frequencyHz) upper++;
        var t = Math.Log(frequencyHz / centres[upper - 1]) / Math.Log((double)centres[upper] / centres[upper - 1]);
        return octaveValues[upper - 1] + (octaveValues[upper] - octaveValues[upper - 1]) * t;
    }

    /// <summary>The index (0 to 7) of the octave band a frequency lies in: the band whose centre is nearest on a logarithmic scale.</summary>
    public static int OctaveBandOf(double frequencyHz)
    {
        var best = 0;
        for (var i = 1; i < 8; i++)
            if (Math.Abs(Math.Log(frequencyHz / OctaveSpectrum.Frequencies[i])) < Math.Abs(Math.Log(frequencyHz / OctaveSpectrum.Frequencies[best]))) best = i;
        return best;
    }

    /// <summary>
    /// The 23 third-octave bands at <paramref name="receiver"/> for the room, source and options given. Throws what
    /// <see cref="EnergyRayTracer.TraceReceivers"/> and <see cref="ExtendedSourceTracer.TraceReceivers"/> throw for a bad source, receiver or options,
    /// and <see cref="ArgumentException"/> for a surface material with no absorption data.
    /// </summary>
    public static IReadOnlyList<SimulatedThirdOctaveBand> Calculate(Room3D room, ExtendedSource source, Vec3 receiver, RayTraceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new RayTraceOptions();
        var nominal = MeasuredThirdOctaveResponse.NominalCentresHz;
        var exact = nominal.Select(MeasuredThirdOctaveResponse.ExactCentreHz).ToArray();

        var absorption = room.Faces.Select(f => OctaveValues(f.Finish.Material.ToOctaveSpectrum())).ToArray();
        var scattering = room.Faces.Select(f => OctaveValues(f.Finish.ScatteringCoefficients)).ToArray();

        var result = new List<SimulatedThirdOctaveBand>();
        for (var start = 0; start < nominal.Count; start += 8)
        {
            // eight slots; the last pass repeats its final band to fill them
            var slots = Enumerable.Range(start, 8).Select(i => Math.Min(i, nominal.Count - 1)).ToArray();
            var centres = slots.Select(i => exact[i]).ToArray();

            var octaveOf = centres.Select(OctaveBandOf).ToArray();
            var passRoom = room.WithFinishes((k, f) => new SurfaceFinish(
                new AbsorptionMaterial(f.Finish.Material.Name, f.Finish.Material.Category,
                    centres.Select(c => (double?)Math.Clamp(Interpolate(absorption[k], c), 0, 1)).ToArray()),
                new OctaveSpectrum(centres.Select(c => Math.Clamp(Interpolate(scattering[k], c), 0, 1)).ToArray()),
                Remap(f.Finish.TransmissionLossDb, octaveOf)));

            var passSource = new ExtendedSource(source.Points.Select(p => p with { Directivity = Remap(p.Directivity, octaveOf) }));
            // the progress of each pass reported as its share of the whole
            var passes = (nominal.Count + 7) / 8;
            var pass = start / 8;
            var passOptions = options with
            {
                Directivity = Remap(options.Directivity, octaveOf), BandCentresHz = centres,
                Progress = options.Progress is { } report ? p => report((pass + p) / passes) : null,
            };

            var response = ExtendedSourceTracer.TraceReceivers(passRoom, passSource, new[] { receiver }, passOptions)[0];
            var metrics = RoomAcousticsCalculator.Calculate(response);

            for (var slot = 0; slot < 8 && start + slot < nominal.Count; slot++)
                result.Add(new SimulatedThirdOctaveBand(nominal[start + slot], exact[start + slot],
                    metrics.EarlyDecayTimeS[slot], metrics.T20S[slot], metrics.T30S[slot], metrics.C50Db[slot], metrics.C80Db[slot],
                    metrics.D50[slot], metrics.CentreTimeS[slot], metrics.StrengthGDb[slot]));
        }
        return result;
    }

    /// <summary>
    /// The room's metrics in one third-octave band at each of many receivers (for a map): the same single pass as <see cref="Calculate"/> uses for a band,
    /// with the surfaces' absorption and scattering interpolated to the band's exact centre and the source's pattern taken from the octave band it lies in.
    /// All eight slots of the tracer are given that one centre, so the time is that of an octave map; read the metrics at index 0.
    /// </summary>
    /// <param name="nominalHz">One of <see cref="MeasuredThirdOctaveResponse.NominalCentresHz"/>.</param>
    public static IReadOnlyList<RoomAcousticMetrics> CalculateAt(Room3D room, ExtendedSource source, IReadOnlyList<Vec3> receivers, int nominalHz, RayTraceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(receivers);
        options ??= new RayTraceOptions();
        var nominal = MeasuredThirdOctaveResponse.NominalCentresHz;
        var position = nominal.ToList().IndexOf(nominalHz);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(nominalHz), nominalHz, "That is not one of the third-octave centre frequencies, 50 Hz to 8 kHz.");
        var centre = MeasuredThirdOctaveResponse.ExactCentreHz(nominalHz);
        var centres = Enumerable.Repeat(centre, 8).ToArray();

        var octaveOf = centres.Select(OctaveBandOf).ToArray();
        var passRoom = room.WithFinishes((_, f) => new SurfaceFinish(
            new AbsorptionMaterial(f.Finish.Material.Name, f.Finish.Material.Category,
                centres.Select(c => (double?)Math.Clamp(Interpolate(OctaveValues(f.Finish.Material.ToOctaveSpectrum()), c), 0, 1)).ToArray()),
            new OctaveSpectrum(centres.Select(c => Math.Clamp(Interpolate(OctaveValues(f.Finish.ScatteringCoefficients), c), 0, 1)).ToArray()),
            Remap(f.Finish.TransmissionLossDb, octaveOf)));

        var passSource = new ExtendedSource(source.Points.Select(p => p with { Directivity = Remap(p.Directivity, octaveOf) }));
        var passOptions = options with { Directivity = Remap(options.Directivity, octaveOf), BandCentresHz = centres };
        return ExtendedSourceTracer.TraceReceivers(passRoom, passSource, receivers, passOptions).Select(RoomAcousticsCalculator.Calculate).ToList();
    }

    private static double[] OctaveValues(OctaveSpectrum s) => Enumerable.Range(0, 8).Select(i => s[i]).ToArray();

    /// <summary>A pattern for the eight slots of a pass: the slot's octave band of the original pattern (null stays null).</summary>
    /// <summary>A sound reduction by octave band given to each slot of a pass from the octave its band falls in.</summary>
    private static OctaveSpectrum? Remap(OctaveSpectrum? octaves, int[] octaveOfSlot) =>
        octaves is { } r ? new OctaveSpectrum(octaveOfSlot.Select(o => r[o]).ToArray()) : null;

    private static SourceDirectivity? Remap(SourceDirectivity? pattern, int[] octaveOfSlot) =>
        pattern is null ? null : SourceDirectivity.PerBand(octaveOfSlot.Select(pattern.ForBand).ToArray());
}
