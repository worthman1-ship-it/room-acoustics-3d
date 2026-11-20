namespace RoomAcoustics.Core;

/// <summary>
/// Atmospheric absorption of sound, ported from the author's `Air` VBA module
/// (`air_absorption` and helpers, Richard King, 6 December 2013) - the
/// ISO 9613-1:1993 formulae: oxygen and nitrogen relaxation frequencies from the molar
/// concentration of water vapour, which comes from the saturation vapour
/// pressure.
/// </summary>
/// <remarks>
/// The source converts °C to K with +273.16 rather than +273.15 - a 0.01 K
/// difference, kept for fidelity. Validated against ISO 9613-2's tabulated
/// attenuation coefficients (six temperature/humidity conditions,
/// 63 Hz - 8 kHz): evaluated at the exact midband frequencies, every value
/// agrees to the table's own precision. Another implementation seen uses
/// 0.01278 instead of 0.01275 for the oxygen term; the tabulated values can't
/// distinguish the two (a 0.2% change to one term), so the VBA's 0.01275 is kept.
/// </remarks>
public static class AirAbsorption
{
    public const double ReferencePressurePa = 101325;
    private const double ReferenceTemperatureK = 293.15;

    public static double SaturationPressurePa(double temperatureK) =>
        ReferencePressurePa * Math.Pow(10, -6.8346 * Math.Pow(273.16 / temperatureK, 1.261) + 4.6151);

    /// <summary>Molar concentration of water vapour, as a percentage.</summary>
    public static double MolarConcentrationPercent(double relativeHumidityPercent, double pressurePa, double temperatureK) =>
        relativeHumidityPercent * (SaturationPressurePa(temperatureK) / ReferencePressurePa) / (pressurePa / ReferencePressurePa);

    public static double OxygenRelaxationFrequencyHz(double pressurePa, double h) =>
        pressurePa / ReferencePressurePa * (24 + 40400 * h * ((0.02 + h) / (0.391 + h)));

    public static double NitrogenRelaxationFrequencyHz(double pressurePa, double h, double temperatureK) =>
        pressurePa / ReferencePressurePa * Math.Pow(temperatureK / ReferenceTemperatureK, -0.5)
        * (9 + 280 * h * Math.Exp(-4.17 * (Math.Pow(temperatureK / ReferenceTemperatureK, -1.0 / 3) - 1)));

    /// <summary>Attenuation coefficient, dB per metre.</summary>
    /// <param name="relativeHumidityPercent">Relative humidity as a percentage, e.g. 50 (not 0.5).</param>
    public static double AttenuationDbPerMetre(double frequencyHz, double pressurePa, double temperatureC, double relativeHumidityPercent)
    {
        var t = temperatureC + 273.16;
        var h = MolarConcentrationPercent(relativeHumidityPercent, pressurePa, t);
        var frO = OxygenRelaxationFrequencyHz(pressurePa, h);
        var frN = NitrogenRelaxationFrequencyHz(pressurePa, h, t);
        var tRel = t / ReferenceTemperatureK;
        var pRel = pressurePa / ReferencePressurePa;
        var f2 = frequencyHz * frequencyHz;

        var classical = 1.84e-11 / pRel * Math.Sqrt(tRel);
        var relaxation = Math.Pow(tRel, -2.5) * (
            0.01275 * Math.Exp(-2239.1 / t) / (frO + f2 / frO)
            + 0.1068 * Math.Exp(-3352 / t) / (frN + f2 / frN));
        return 8.686 * f2 * (classical + relaxation);
    }

    /// <summary>
    /// Air absorption as an equivalent absorption area, 4mV (m²), where m is the
    /// intensity attenuation coefficient in Np/m (dB/m ÷ 10 log10 e).
    /// </summary>
    public static double EquivalentAbsorptionAreaM2(double frequencyHz, double roomVolumeM3, double temperatureC, double relativeHumidityPercent, double pressurePa = ReferencePressurePa) =>
        4 * AttenuationDbPerMetre(frequencyHz, pressurePa, temperatureC, relativeHumidityPercent) * roomVolumeM3 / (10 * Math.Log10(Math.E));
}
