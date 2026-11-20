using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using PureHDF;

namespace RoomAcoustics.Core;

/// <summary>
/// A measured head-related transfer function, read from a SOFA file (AES69, the SimpleFreeFieldHRIR convention): for each measured direction, each ear's
/// power gain in the eight octave bands, so that it can stand in for the spherical head of <see cref="HeadShadow"/> in the energy-based binaural rendering.
/// </summary>
/// <remarks>
/// <para>
/// The file holds <c>Data.IR</c>, an impulse response for each measurement and each ear (M x 2 x N), <c>Data.SamplingRate</c>, and <c>SourcePosition</c>, where
/// each measurement's source was relative to the listener: azimuth (degrees anticlockwise from straight ahead, so 90 is the left), elevation (degrees up) and
/// distance, or x (ahead), y (left), z (up) when its <c>Type</c> attribute says cartesian. The listener is taken to face +x with +z up, as the convention's
/// default listener view. The left ear is the receiver with the larger y in <c>ReceiverPosition</c>, or the first if that is not given.
/// </para>
/// <para>
/// Each band's power gain is the mean of |H(f)|² over the band (f/√2 to f√2), from the impulse response padded to at least 8192 samples so the low bands have
/// frequencies in them. The gains are then divided, band by band, by their mean over all the measurements and both ears (diffuse-field equalisation), so that
/// in a diffuse field an ear hears the same energy as without a head, as with the spherical model; what is left is how the head and ears colour sound from
/// each direction. A direction between measurements takes the nearest one. Only the magnitude is used: the delay between the ears is still Woodworth's.
/// </para>
/// </remarks>
public sealed class MeasuredHrtf
{
    private const int Step = 5; // degrees of the lookup table from direction to nearest measurement
    private readonly double[][][] _gains; // [ear][measurement][band]
    private readonly Vec3[] _directions;
    private readonly int[] _nearest;

    private MeasuredHrtf(string name, double sampleRateHz, Vec3[] directions, double[][][] gains)
    {
        Name = name;
        SampleRateHz = sampleRateHz;
        _directions = directions;
        _gains = gains;
        // the nearest measurement to each cell of a 5 degree grid of azimuth and elevation
        _nearest = new int[(360 / Step) * (180 / Step + 1)];
        for (var a = 0; a < 360 / Step; a++)
            for (var e = 0; e <= 180 / Step; e++)
            {
                var d = Direction(a * Step, e * Step - 90);
                var best = 0;
                for (var m = 1; m < directions.Length; m++)
                    if (d.Dot(directions[m]) > d.Dot(directions[best])) best = m;
                _nearest[a * (180 / Step + 1) + e] = best;
            }
    }

    public string Name { get; }
    public double SampleRateHz { get; }
    public int Measurements => _directions.Length;

    /// <summary>
    /// The power gain of one ear (0 left, 1 right) in an octave band for sound coming from <paramref name="fromHead"/>, a direction in the head's own axes
    /// (x ahead, y to the left, z up; it need not be a unit vector).
    /// </summary>
    public double PowerGain(int ear, int band, Vec3 fromHead)
    {
        var length = fromHead.Length;
        if (length == 0) return 1;
        var azimuth = Math.Atan2(fromHead.Y, fromHead.X) * 180 / Math.PI;
        var elevation = Math.Asin(Math.Clamp(fromHead.Z / length, -1, 1)) * 180 / Math.PI;
        var a = (int)Math.Round((azimuth + 360) % 360 / Step) % (360 / Step);
        var e = (int)Math.Round((elevation + 90) / Step);
        return _gains[ear][_nearest[a * (180 / Step + 1) + e]][band];
    }

    public static MeasuredHrtf Read(Stream stream, string name = "HRTF")
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        try
        {
            using var file = H5File.Open(memory, leaveOpen: true);
            if (!file.LinkExists("Data.IR") || !file.LinkExists("SourcePosition"))
                throw new FormatException("This is not a SOFA HRTF file: it has no Data.IR or SourcePosition.");
            var irSet = file.Dataset("Data.IR");
            var shape = irSet.Space.Dimensions;
            if (shape.Length != 3 || shape[1] != 2) throw new FormatException("Data.IR must hold an impulse response for each measurement and each of two ears (M x 2 x N).");
            int m = (int)shape[0], n = (int)shape[2];
            if (m < 1 || n < 1) throw new FormatException("Data.IR is empty.");
            var ir = Doubles(irSet);
            var rate = Doubles(file.Dataset("Data.SamplingRate"))[0];
            if (!(rate > 0)) throw new FormatException("The sampling rate must be positive.");

            var positionSet = file.Dataset("SourcePosition");
            var positions = Doubles(positionSet);
            if (positions.Length != 3 * m && positions.Length != 3) throw new FormatException("SourcePosition must give a position for each measurement.");
            var cartesian = TextAttribute(positionSet, "Type")?.Contains("cartesian", StringComparison.OrdinalIgnoreCase) == true;
            var directions = Enumerable.Range(0, m).Select(i =>
            {
                var o = positions.Length == 3 ? 0 : 3 * i;
                return cartesian ? new Vec3(positions[o], positions[o + 1], positions[o + 2]).Normalized() : Direction(positions[o], positions[o + 1]);
            }).ToArray();

            // the left ear is the one further to +y
            var left = 0;
            if (file.LinkExists("ReceiverPosition"))
            {
                var receivers = Doubles(file.Dataset("ReceiverPosition"));
                if (receivers.Length >= 6 && receivers[4] > receivers[1]) left = 1;
            }

            var bandPower = new double[2][][];
            for (var ear = 0; ear < 2; ear++)
            {
                var r = ear == 0 ? left : 1 - left;
                bandPower[ear] = Enumerable.Range(0, m).Select(i => BandPowers(ir.AsSpan((i * 2 + r) * n, n), rate)).ToArray();
            }
            for (var band = 0; band < 8; band++)
            {
                var mean = bandPower.SelectMany(e => e).Average(p => p[band]);
                if (mean <= 0) throw new FormatException("The impulse responses are silent.");
                foreach (var ear in bandPower) foreach (var p in ear) p[band] /= mean;
            }
            var title = TextAttribute(file, "Title");
            return new MeasuredHrtf(string.IsNullOrWhiteSpace(title) ? name : $"{title} ({name})", rate, directions, bandPower);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException($"The file could not be read as a SOFA (HDF5) file: {ex.Message}", ex);
        }
    }

    private static double[] Doubles(IH5Dataset set)
    {
        try { return set.Read<double[]>(); }
        catch (Exception) { return set.Read<float[]>().Select(v => (double)v).ToArray(); }
    }

    private static string? TextAttribute(IH5Object owner, string name)
    {
        try { return owner.AttributeExists(name) ? owner.Attribute(name).Read<string>() : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The unit vector of an azimuth (degrees anticlockwise from +x) and elevation (degrees up).</summary>
    private static Vec3 Direction(double azimuthDeg, double elevationDeg)
    {
        double az = azimuthDeg * Math.PI / 180, el = elevationDeg * Math.PI / 180;
        return new Vec3(Math.Cos(el) * Math.Cos(az), Math.Cos(el) * Math.Sin(az), Math.Sin(el));
    }

    /// <summary>The mean of |H(f)|² over each octave band, from the impulse response padded to at least 8192 samples.</summary>
    private static double[] BandPowers(ReadOnlySpan<double> ir, double rate)
    {
        var size = 8192;
        while (size < ir.Length) size <<= 1;
        var spectrum = new Complex[size];
        for (var i = 0; i < ir.Length; i++) spectrum[i] = ir[i];
        Fourier.Forward(spectrum, FourierOptions.Matlab);
        var powers = new double[8];
        for (var band = 0; band < 8; band++)
        {
            double centre = OctaveSpectrum.Frequencies[band], low = centre / Math.Sqrt(2), high = centre * Math.Sqrt(2);
            int from = (int)Math.Ceiling(low * size / rate), to = Math.Min(size / 2, (int)Math.Floor(high * size / rate));
            if (to < from) to = from = Math.Min(size / 2, (int)Math.Round(centre * size / rate)); // above the file's Nyquist frequency: its last bin
            var sum = 0.0;
            for (var k = from; k <= to; k++) sum += spectrum[k].Magnitude * spectrum[k].Magnitude;
            powers[band] = sum / (to - from + 1);
        }
        return powers;
    }
}
