namespace RoomAcoustics.Core;

/// <summary>
/// Sabine reverberation time calculation. The original "Reverberation Time
/// Calculator" tool (Form1.vb) only ever summed a single surface
/// (T60 = 0.16*V/(alpha*S)); this completes the same Sabine formula properly
/// across every surface in the room: T60 = 0.161*V / sum(S_i * alpha_i).
/// The 0.161 constant matches the more complete acoustic_calculations.vb
/// engine (functions revlp/room_to_room), rather than the 0.16 used in the
/// standalone calculator stub.
/// </summary>
public static class ReverberationCalculator
{
    private const double SabineConstant = 0.161;

    /// <summary>Total absorption (sum of area * alpha) per octave band across all surfaces, in sabins.</summary>
    public static OctaveSpectrum TotalAbsorption(IReadOnlyList<Surface> surfaces)
    {
        if (surfaces.Count == 0)
            throw new ArgumentException("At least one surface is required.", nameof(surfaces));

        var totals = new double[8];
        foreach (var surface in surfaces)
        {
            var alpha = surface.Material.ToOctaveSpectrum();
            for (var i = 0; i < 8; i++)
                totals[i] += surface.AreaSqm * alpha[i];
        }

        return new OctaveSpectrum(totals);
    }

    /// <summary>
    /// Reverberation time (T60, seconds) per octave band, via the Sabine
    /// formula T60 = 0.161*V / sum(S*alpha).
    /// </summary>
    public static OctaveSpectrum CalculateT60(double roomVolumeCubicMetres, IReadOnlyList<Surface> surfaces)
    {
        if (roomVolumeCubicMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(roomVolumeCubicMetres), "Room volume must be positive.");

        var absorption = TotalAbsorption(surfaces);
        var t60 = new double[8];
        for (var i = 0; i < 8; i++)
        {
            if (absorption[i] <= 0)
                throw new InvalidOperationException(
                    $"Total absorption in the {OctaveSpectrum.Frequencies[i]} Hz band is zero - check surface areas and absorption coefficients.");
            t60[i] = SabineConstant * roomVolumeCubicMetres / absorption[i];
        }
        return new OctaveSpectrum(t60);
    }

    /// <summary>
    /// T60 per the author's earlier `rev_time`/`sabine`/`eyring` VBA
    /// (Reverberation_Calculations module): 55.3 V / (c (A + A_air)), with c =
    /// 331.3 + 0.606 t, the surface absorption A = ᾱS for Sabine or -S ln(1 - ᾱ)
    /// for Eyring-Norris (ᾱ the area-weighted mean coefficient over the total
    /// surface area S), and optional air absorption A_air = 4mV
    /// (<see cref="AirAbsorption.EquivalentAbsorptionAreaM2"/>) evaluated at the
    /// nominal octave-band frequencies, as that code does.
    /// </summary>
    public static ReverberationResult Calculate(double roomVolumeCubicMetres, IReadOnlyList<Surface> surfaces, ReverberationOptions options)
    {
        if (roomVolumeCubicMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(roomVolumeCubicMetres), "Room volume must be positive.");

        var totalArea = surfaces.Sum(s => s.AreaSqm);
        var absorption = TotalAbsorption(surfaces);
        var speedOfSound = 331.3 + 0.606 * options.TemperatureC;
        var meanAlpha = new double[8];
        var airArea = new double[8];
        var t60 = new double[8];
        for (var i = 0; i < 8; i++)
        {
            meanAlpha[i] = totalArea > 0 ? absorption[i] / totalArea : 0;
            airArea[i] = options.IncludeAirAbsorption
                ? AirAbsorption.EquivalentAbsorptionAreaM2(OctaveSpectrum.Frequencies[i], roomVolumeCubicMetres, options.TemperatureC, options.RelativeHumidityPercent)
                : 0;
            t60[i] = options.Method switch
            {
                ReverberationMethod.ArauPuchades or ReverberationMethod.Fitzroy => DirectionalT60(options.Method, i, roomVolumeCubicMetres, surfaces, totalArea, speedOfSound, airArea[i]),
                _ => T60From(roomVolumeCubicMetres, speedOfSound, i, SurfaceAbsorption(options.Method, i, surfaces, totalArea, meanAlpha[i]) + airArea[i]),
            };
        }

        return new ReverberationResult(new OctaveSpectrum(t60), new OctaveSpectrum(meanAlpha), new OctaveSpectrum(airArea), totalArea);
    }

    // an absorption coefficient of 1 would make ln(1 - a) infinite: the Eyring-type equations are taken at 0.999 for it
    private const double MaxAlphaInLogarithm = 0.999;

    private static double T60From(double volume, double speedOfSound, int band, double absorptionArea)
    {
        var denominator = speedOfSound * absorptionArea;
        if (denominator <= 0)
            throw new InvalidOperationException(
                $"Total absorption in the {OctaveSpectrum.Frequencies[band]} Hz band is zero - check surface areas and absorption coefficients.");
        return 55.3 * volume / denominator;
    }

    /// <summary>The surface absorption area of one band: Sabine's a S, Eyring-Norris's -S ln(1 - a) of the mean a, or Millington-Sette's sum of -S_i ln(1 - a_i).</summary>
    private static double SurfaceAbsorption(ReverberationMethod method, int band, IReadOnlyList<Surface> surfaces, double totalArea, double meanAlpha) => method switch
    {
        ReverberationMethod.EyringNorris => -totalArea * Math.Log(1 - Math.Min(meanAlpha, MaxAlphaInLogarithm)),
        ReverberationMethod.Millington => surfaces.Sum(s => -s.AreaSqm * Math.Log(1 - Math.Min(s.Material.ToOctaveSpectrum()[band], MaxAlphaInLogarithm))),
        _ => meanAlpha * totalArea,
    };

    /// <summary>
    /// Arau-Puchades and Fitzroy, which allow for the absorption being uneven between the three pairs of room faces: each pair (the surfaces of one
    /// <see cref="Surface.Axis"/>) has its own Eyring time T_a = 55.3 V / (c (-S ln(1 - mean a of the pair) + A_air)) over all the room's surface S, and the
    /// room's time is the area-weighted geometric mean of them (Arau-Puchades) or arithmetic mean (Fitzroy). Air absorption is added to every pair's
    /// absorption area; neither author's equation includes it, so that is this program's choice. With one absorption everywhere both equal Eyring's.
    /// </summary>
    private static double DirectionalT60(ReverberationMethod method, int band, double volume, IReadOnlyList<Surface> surfaces, double totalArea, double speedOfSound, double airArea)
    {
        if (surfaces.Any(s => s.Axis is < 0 or > 2))
            throw new ArgumentException($"{method} needs every surface to be given an axis (0, 1 or 2: the faces perpendicular to x, y or z).", nameof(surfaces));
        double geometric = 1, arithmetic = 0;
        foreach (var axis in surfaces.GroupBy(s => s.Axis))
        {
            var area = axis.Sum(s => s.AreaSqm);
            var alpha = Math.Min(axis.Sum(s => s.AreaSqm * s.Material.ToOctaveSpectrum()[band]) / area, MaxAlphaInLogarithm);
            var time = T60From(volume, speedOfSound, band, -totalArea * Math.Log(1 - alpha) + airArea);
            geometric *= Math.Pow(time, area / totalArea);
            arithmetic += time * area / totalArea;
        }
        return method == ReverberationMethod.ArauPuchades ? geometric : arithmetic;
    }
}

public enum ReverberationMethod { Sabine, EyringNorris, Millington, ArauPuchades, Fitzroy }

public sealed record ReverberationOptions(
    ReverberationMethod Method = ReverberationMethod.Sabine,
    bool IncludeAirAbsorption = false,
    double TemperatureC = 20,
    double RelativeHumidityPercent = 60);

public sealed record ReverberationResult(OctaveSpectrum T60, OctaveSpectrum MeanAlpha, OctaveSpectrum AirAbsorptionAreaM2, double TotalSurfaceAreaM2);
