using System.Globalization;

namespace RoomAcoustics.Core;

/// <summary>
/// A measured or published directivity balloon: a source's level in every direction, in each octave band, on a grid of azimuth and elevation.
/// Unlike <see cref="PolarDirectivity"/> it need not be the same all round an axis, so it can describe a loudspeaker with a horn that is wide
/// and narrow in different planes, a person, an instrument, a cabinet with a rear port. Aimed along an axis it gives the
/// <see cref="SourceDirectivity"/> the tracer uses, with a pattern of its own in each band.
/// </summary>
/// <remarks>
/// <para>
/// The balloon is in the source's own frame. Azimuth is the angle in the horizontal plane from the front (0°) round to the left (90°), to the
/// back (180°) and the right (270°), the way compass angles run in the plan; elevation is the angle up from that plane, from -90° (straight
/// down) to 90° (straight up). The front, 0° azimuth and 0° elevation, is the axis the source is aimed along; "up" is the world's up, so
/// a source aimed along the plan has its elevation 90° straight up, and one tilted up has its balloon tilted with it, without roll.
/// </para>
/// <para>
/// Levels are in dB against any reference; only their shape matters, because each band is normalised so that the radiated power is unchanged.
/// Between grid points the level is interpolated bilinearly in dB, wrapping round in azimuth, and beyond the elevations given it stays at the
/// end rows.
/// </para>
/// </remarks>
public sealed class BalloonDirectivity
{
    /// <summary>The azimuths of the grid in degrees, increasing, each from 0 up to but not including 360.</summary>
    public IReadOnlyList<double> AzimuthsDeg { get; }

    /// <summary>The elevations of the grid in degrees, increasing, each from -90 to 90.</summary>
    public IReadOnlyList<double> ElevationsDeg { get; }

    private readonly double[][][] _levels;   // [band][elevation][azimuth], dB

    /// <param name="azimuthsDeg">At least three azimuths, increasing, each from 0 up to below 360.</param>
    /// <param name="elevationsDeg">At least two elevations, increasing, each from -90 to 90.</param>
    /// <param name="levelsDb">For each of the eight octave bands 63 Hz to 8 kHz, the level at each elevation (outer) and azimuth (inner).</param>
    public BalloonDirectivity(IReadOnlyList<double> azimuthsDeg, IReadOnlyList<double> elevationsDeg, IReadOnlyList<double[][]> levelsDb)
    {
        ArgumentNullException.ThrowIfNull(azimuthsDeg);
        ArgumentNullException.ThrowIfNull(elevationsDeg);
        ArgumentNullException.ThrowIfNull(levelsDb);
        if (azimuthsDeg.Count < 3) throw new ArgumentException("A balloon needs at least three azimuths.", nameof(azimuthsDeg));
        if (elevationsDeg.Count < 2) throw new ArgumentException("A balloon needs at least two elevations.", nameof(elevationsDeg));
        if (azimuthsDeg.Any(a => !double.IsFinite(a) || a < 0 || a >= 360))
            throw new ArgumentException("Azimuths must be from 0 up to but not including 360 degrees.", nameof(azimuthsDeg));
        if (elevationsDeg.Any(e => !double.IsFinite(e) || e < -90 || e > 90))
            throw new ArgumentException("Elevations must be from -90 to 90 degrees.", nameof(elevationsDeg));
        for (var i = 1; i < azimuthsDeg.Count; i++)
            if (azimuthsDeg[i] <= azimuthsDeg[i - 1]) throw new ArgumentException("Azimuths must be in increasing order, with none repeated.", nameof(azimuthsDeg));
        for (var i = 1; i < elevationsDeg.Count; i++)
            if (elevationsDeg[i] <= elevationsDeg[i - 1]) throw new ArgumentException("Elevations must be in increasing order, with none repeated.", nameof(elevationsDeg));
        if (levelsDb.Count != 8) throw new ArgumentException("There must be one grid of levels for each of the eight octave bands.", nameof(levelsDb));
        foreach (var band in levelsDb)
        {
            if (band.Length != elevationsDeg.Count || band.Any(row => row.Length != azimuthsDeg.Count))
                throw new ArgumentException("Every band needs one level for each elevation and azimuth.", nameof(levelsDb));
            if (band.Any(row => row.Any(v => !double.IsFinite(v))))
                throw new ArgumentException("Levels must be finite numbers of dB.", nameof(levelsDb));
        }

        AzimuthsDeg = azimuthsDeg.ToArray();
        ElevationsDeg = elevationsDeg.ToArray();
        _levels = levelsDb.Select(band => band.Select(row => row.ToArray()).ToArray()).ToArray();
    }

    /// <summary>
    /// A balloon from a loudspeaker's published coverage: the angles, horizontal and vertical, between the points on either side of the axis
    /// where the level is 6 dB down. The level falls off as the square of the angle in each plane (a parabola in dB, which is close to a real
    /// horn inside its coverage) and never goes below <paramref name="floorDb"/>, the level outside the beam. It is the same in every octave band,
    /// as for a constant-directivity horn above its lowest frequency.
    /// </summary>
    /// <param name="horizontalDeg">The -6 dB coverage in the horizontal plane, above 0 up to 360 degrees.</param>
    /// <param name="verticalDeg">The -6 dB coverage in the vertical plane, above 0 up to 180 degrees.</param>
    /// <param name="floorDb">The lowest level, in dB below the axis: from -60 to 0 (0 makes the source omnidirectional).</param>
    public static BalloonDirectivity FromCoverage(double horizontalDeg, double verticalDeg, double floorDb = -20)
    {
        if (!(horizontalDeg > 0 && horizontalDeg <= 360))
            throw new ArgumentOutOfRangeException(nameof(horizontalDeg), "The horizontal coverage must be above 0 and up to 360 degrees.");
        if (!(verticalDeg > 0 && verticalDeg <= 180))
            throw new ArgumentOutOfRangeException(nameof(verticalDeg), "The vertical coverage must be above 0 and up to 180 degrees.");
        if (!(floorDb >= -60 && floorDb <= 0))
            throw new ArgumentOutOfRangeException(nameof(floorDb), "The level outside the beam must be from -60 to 0 dB.");

        const double step = 2.5;
        var azimuths = Enumerable.Range(0, (int)(360 / step)).Select(i => i * step).ToArray();
        var elevations = Enumerable.Range(0, (int)(180 / step) + 1).Select(i => -90 + i * step).ToArray();
        var grid = elevations.Select(el => azimuths.Select(az =>
        {
            var off = az > 180 ? az - 360 : az;
            // the horizontal angle is shortened by cos(elevation), so the pattern is the same all round at the poles
            var h = off * Math.Cos(el * Math.PI / 180) / (horizontalDeg / 2);
            var v = el / (verticalDeg / 2);
            return Math.Max(floorDb, -6 * (h * h + v * v));
        }).ToArray()).ToArray();
        return new BalloonDirectivity(azimuths, elevations, Enumerable.Repeat(grid, 8).ToArray());
    }

    /// <summary>
    /// A human talker, measured: Chu and Warnock's balloon of 40 talkers (see <see cref="TalkerDirectivity"/>), facing along the front axis. Its
    /// directivity index on axis is about 1.5 dB at 63 Hz to 1 kHz and 4 dB at 2 to 8 kHz.
    /// </summary>
    public static BalloonDirectivity Talker()
    {
        // the report measured one side; the other is its mirror image, azimuth a and 360 - a alike
        var oneSide = TalkerDirectivity.AzimuthsDeg;
        var azimuths = oneSide.Concat(oneSide.Skip(1).Take(oneSide.Length - 2).Reverse().Select(a => 360 - a)).ToArray();
        int Column(double azimuth) => Array.IndexOf(oneSide, azimuth <= 180 ? azimuth : 360 - azimuth);
        var levels = TalkerDirectivity.LevelsDb.Select(band => band.Select(row => azimuths.Select(az => row[Column(az)]).ToArray()).ToArray()).ToArray();
        return new BalloonDirectivity(azimuths, TalkerDirectivity.ElevationsDeg, levels);
    }

    /// <summary>The level in one band in a direction of the source's frame, interpolated in dB.</summary>
    public double LevelDb(int bandIndex, double azimuthDeg, double elevationDeg)
    {
        if (bandIndex < 0 || bandIndex > 7) throw new ArgumentOutOfRangeException(nameof(bandIndex), "The band index must be 0 to 7.");
        var grid = _levels[bandIndex];

        // elevation: clamp, then find the rows either side
        var el = Math.Clamp(elevationDeg, ElevationsDeg[0], ElevationsDeg[^1]);
        var rowHigh = 1;
        while (rowHigh < ElevationsDeg.Count - 1 && ElevationsDeg[rowHigh] < el) rowHigh++;
        var rowLow = rowHigh - 1;
        var tEl = (el - ElevationsDeg[rowLow]) / (ElevationsDeg[rowHigh] - ElevationsDeg[rowLow]);

        // azimuth: periodic, so the last column joins the first one 360 degrees on
        var az = azimuthDeg % 360;
        if (az < 0) az += 360;
        int colLow, colHigh;
        double tAz;
        if (az >= AzimuthsDeg[^1])
        {
            colLow = AzimuthsDeg.Count - 1;
            colHigh = 0;
            tAz = (az - AzimuthsDeg[^1]) / (AzimuthsDeg[0] + 360 - AzimuthsDeg[^1]);
        }
        else if (az < AzimuthsDeg[0])
        {
            colLow = AzimuthsDeg.Count - 1;
            colHigh = 0;
            tAz = (az + 360 - AzimuthsDeg[^1]) / (AzimuthsDeg[0] + 360 - AzimuthsDeg[^1]);
        }
        else
        {
            colHigh = 1;
            while (AzimuthsDeg[colHigh] <= az) colHigh++;
            colLow = colHigh - 1;
            tAz = (az - AzimuthsDeg[colLow]) / (AzimuthsDeg[colHigh] - AzimuthsDeg[colLow]);
        }

        double Row(int row) => grid[row][colLow] + tAz * (grid[row][colHigh] - grid[row][colLow]);
        return Row(rowLow) + tEl * (Row(rowHigh) - Row(rowLow));
    }

    /// <summary>
    /// The source's directivity with its front pointing along <paramref name="forward"/>. "Up" is <paramref name="up"/> (the world's up if
    /// null), made square to the front; if the front is straight along it, the plan's +y is the source's left.
    /// </summary>
    public SourceDirectivity Aimed(Vec3 forward, Vec3? up = null)
    {
        var front = forward.Normalized();
        var left = (up ?? new Vec3(0, 0, 1)).Cross(front);
        left = left.Length < 1e-9 ? new Vec3(0, 1, 0) : left.Normalized();
        var above = front.Cross(left);

        var bands = new SourceDirectivity[8];
        for (var b = 0; b < 8; b++)
        {
            var band = b;
            bands[b] = new SourceDirectivity(d =>
            {
                var x = d.Dot(front);
                var y = d.Dot(left);
                var z = Math.Clamp(d.Dot(above), -1, 1);
                var azimuth = Math.Atan2(y, x) * 180 / Math.PI;
                var elevation = Math.Asin(z) * 180 / Math.PI;
                return Math.Pow(10, LevelDb(band, azimuth, elevation) / 10);
            });
        }
        return SourceDirectivity.PerBand(bands);
    }

    /// <summary>
    /// Reads a balloon as text: a header line, then one line per direction. Commas, semicolons or tabs separate the values, blank lines and
    /// lines starting with # are ignored, and numbers use a full stop for the decimal point. The header is <c>azimuth_deg, elevation_deg</c>
    /// followed either by the eight octave centre frequencies (63, 125 ... 8000, in any order and optionally ending in Hz) or by one column of
    /// levels used for every band. The directions must make a complete grid: every azimuth with every elevation, once each.
    /// </summary>
    public static BalloonDirectivity FromCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count < 7) throw new FormatException("The table needs a header line and at least three azimuths at two elevations.");

        var header = Split(lines[0]);
        var columns = header.Length - 2;
        if (columns != 1 && columns != 8)
            throw new FormatException("The header must be the azimuth and elevation followed by one column of levels, or by the eight octave bands 63 to 8000 Hz.");
        var bandColumn = new int[8];
        Array.Fill(bandColumn, 2);
        if (columns == 8)
        {
            Array.Fill(bandColumn, -1);
            for (var c = 2; c < header.Length; c++)
            {
                var name = header[c].Replace("Hz", "", StringComparison.OrdinalIgnoreCase).Trim();
                var index = double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                    ? Array.IndexOf(OctaveSpectrum.Frequencies, (int)Math.Round(f))
                    : -1;
                if (index < 0 || bandColumn[index] >= 0)
                    throw new FormatException($"The header column '{header[c]}' is not one of the octave bands 63 Hz to 8 kHz, or appears twice.");
                bandColumn[index] = c;
            }
        }

        var points = new Dictionary<(double Az, double El), double[]>();
        for (var row = 1; row < lines.Count; row++)
        {
            var cells = Split(lines[row]);
            if (cells.Length != header.Length)
                throw new FormatException($"Line {row + 1} has {cells.Length} values; the header has {header.Length}.");
            var numbers = cells.Select(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new FormatException($"'{c}' on line {row + 1} is not a number.")).ToArray();
            if (!points.TryAdd((numbers[0], numbers[1]), Enumerable.Range(0, 8).Select(b => numbers[bandColumn[b]]).ToArray()))
                throw new FormatException($"Line {row + 1}: the direction azimuth {numbers[0]:0.###}, elevation {numbers[1]:0.###} is given twice.");
        }

        var azimuths = points.Keys.Select(k => k.Az).Distinct().OrderBy(a => a).ToArray();
        var elevations = points.Keys.Select(k => k.El).Distinct().OrderBy(e => e).ToArray();
        foreach (var el in elevations)
            foreach (var az in azimuths)
                if (!points.ContainsKey((az, el)))
                    throw new FormatException($"The grid is not complete: there is no line for azimuth {az:0.###}, elevation {el:0.###}.");

        try
        {
            var levels = Enumerable.Range(0, 8).Select(b =>
                elevations.Select(el => azimuths.Select(az => points[(az, el)][b]).ToArray()).ToArray()).ToList();
            return new BalloonDirectivity(azimuths, elevations, levels);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    private static string[] Split(string line) => line.Split(',', ';', '\t').Select(c => c.Trim()).ToArray();
}
