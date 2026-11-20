namespace RoomAcoustics.Core;

/// <summary>
/// A sound level spectrum across the eight standard octave bands (63 Hz - 8 kHz).
/// Values may be sound pressure levels, sound power levels or attenuation/loss
/// values in dB, depending on context.
/// </summary>
public readonly struct OctaveSpectrum
{
    public static readonly int[] Frequencies = { 63, 125, 250, 500, 1000, 2000, 4000, 8000 };

    private readonly double[] _bands;

    public OctaveSpectrum(double b63, double b125, double b250, double b500, double b1000, double b2000, double b4000, double b8000)
    {
        _bands = new[] { b63, b125, b250, b500, b1000, b2000, b4000, b8000 };
    }

    public OctaveSpectrum(IReadOnlyList<double> bands)
    {
        if (bands.Count != 8)
            throw new ArgumentException("An octave spectrum must have exactly 8 band values (63 Hz - 8 kHz).", nameof(bands));
        _bands = bands.ToArray();
    }

    /// <summary>A spectrum with every band set to the same value.</summary>
    public static OctaveSpectrum Flat(double value) =>
        new(value, value, value, value, value, value, value, value);

    public double this[int bandIndex] => _bands[bandIndex];

    /// <summary>Look up a band value by its centre frequency (e.g. 1000 for the 1 kHz band).</summary>
    public double AtFrequency(int frequencyHz) => _bands[IndexOfFrequency(frequencyHz)];

    public static int IndexOfFrequency(int frequencyHz)
    {
        var index = Array.IndexOf(Frequencies, frequencyHz);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz,
                "Frequency must be one of the eight standard octave bands: " + string.Join(", ", Frequencies));
        return index;
    }

    public IReadOnlyList<double> Bands => _bands;

    public override string ToString() =>
        string.Join(", ", Frequencies.Zip(_bands, (f, v) => $"{f}Hz={v:0.#}"));
}
