using System.Globalization;
using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>
/// A measured or published polar response of a source that radiates the same all round its axis (a loudspeaker, a
/// horn, a fan outlet): its level against the angle from the axis, in each octave band. Aimed along an axis, it gives the
/// <see cref="SourceDirectivity"/> the tracer uses, with a different pattern in each band, since a source is usually
/// much narrower at 8 kHz than at 63 Hz.
/// </summary>
/// <remarks>
/// Levels are in dB against any reference (usually the on-axis level); only the shape matters, because each band's
/// pattern is normalised so the radiated power is unchanged. Between the angles given the level is interpolated linearly in
/// dB; before the first and after the last angle it stays at the end values.
/// </remarks>
public sealed class PolarDirectivity
{
    /// <summary>The angle from the axis of each row, in degrees from 0 (on axis) to 180 (straight behind), increasing.</summary>
    public IReadOnlyList<double> AnglesDeg { get; }

    /// <summary>For each octave band 63 Hz to 8 kHz, the level in dB at each of <see cref="AnglesDeg"/>.</summary>
    public IReadOnlyList<IReadOnlyList<double>> LevelsDbByBand { get; }

    /// <param name="anglesDeg">At least two angles, increasing, each from 0 to 180.</param>
    /// <param name="levelsDbByBand">Eight lists (63 Hz to 8 kHz) of finite levels, one per angle.</param>
    public PolarDirectivity(IReadOnlyList<double> anglesDeg, IReadOnlyList<IReadOnlyList<double>> levelsDbByBand)
    {
        ArgumentNullException.ThrowIfNull(anglesDeg);
        ArgumentNullException.ThrowIfNull(levelsDbByBand);
        if (anglesDeg.Count < 2) throw new ArgumentException("A polar response needs at least two angles.", nameof(anglesDeg));
        if (anglesDeg.Any(a => !double.IsFinite(a) || a < 0 || a > 180))
            throw new ArgumentException("Angles must be from 0 to 180 degrees.", nameof(anglesDeg));
        for (var i = 1; i < anglesDeg.Count; i++)
            if (anglesDeg[i] <= anglesDeg[i - 1]) throw new ArgumentException("Angles must be in increasing order, with none repeated.", nameof(anglesDeg));
        if (levelsDbByBand.Count != 8) throw new ArgumentException("There must be one list of levels for each of the eight octave bands.", nameof(levelsDbByBand));
        if (levelsDbByBand.Any(l => l.Count != anglesDeg.Count))
            throw new ArgumentException("Every band needs one level for each angle.", nameof(levelsDbByBand));
        if (levelsDbByBand.Any(l => l.Any(v => !double.IsFinite(v))))
            throw new ArgumentException("Levels must be finite numbers of dB.", nameof(levelsDbByBand));

        AnglesDeg = anglesDeg.ToArray();
        LevelsDbByBand = levelsDbByBand.Select(l => (IReadOnlyList<double>)l.ToArray()).ToArray();
    }

    /// <summary>The level in one band at an angle from the axis, interpolated in dB.</summary>
    public double LevelDb(int bandIndex, double angleDeg)
    {
        if (bandIndex < 0 || bandIndex > 7) throw new ArgumentOutOfRangeException(nameof(bandIndex), "The band index must be 0 to 7.");
        var levels = LevelsDbByBand[bandIndex];
        if (angleDeg <= AnglesDeg[0]) return levels[0];
        if (angleDeg >= AnglesDeg[^1]) return levels[^1];
        var hi = 1;
        while (AnglesDeg[hi] < angleDeg) hi++;
        var t = (angleDeg - AnglesDeg[hi - 1]) / (AnglesDeg[hi] - AnglesDeg[hi - 1]);
        return levels[hi - 1] + t * (levels[hi] - levels[hi - 1]);
    }

    /// <summary>The source's directivity with its axis pointing along <paramref name="axis"/>.</summary>
    public SourceDirectivity Aimed(Vec3 axis)
    {
        var unit = axis.Normalized();
        var bands = new SourceDirectivity[8];
        for (var b = 0; b < 8; b++)
        {
            var band = b;
            bands[b] = new SourceDirectivity(d =>
            {
                var angle = Math.Acos(Math.Clamp(d.Dot(unit), -1, 1)) * 180 / Math.PI;
                return Math.Pow(10, LevelDb(band, angle) / 10);
            });
        }
        return SourceDirectivity.PerBand(bands);
    }

    /// <summary>
    /// Reads a table as text: a header line, then one line per angle. Commas, semicolons or tabs separate the values,
    /// blank lines and lines starting with # are ignored, and numbers use a full stop for the decimal point. The header
    /// is <c>angle_deg</c> followed either by the eight octave centre frequencies (63, 125 ... 8000, in any order and
    /// optionally ending in Hz) or by one column of levels used for every band.
    /// </summary>
    public static PolarDirectivity FromCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count < 3) throw new FormatException("The table needs a header line and at least two angles.");

        var header = Split(lines[0]);
        var bandColumn = new int[8];
        Array.Fill(bandColumn, -1);
        var columns = header.Length - 1;
        if (columns == 1)
        {
            Array.Fill(bandColumn, 1);
        }
        else if (columns == 8)
        {
            for (var c = 1; c < header.Length; c++)
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
        else
        {
            throw new FormatException("The header must be the angle followed by one column of levels, or by the eight octave bands 63 to 8000 Hz.");
        }

        var angles = new List<double>();
        var levels = Enumerable.Range(0, 8).Select(_ => new List<double>()).ToArray();
        for (var row = 1; row < lines.Count; row++)
        {
            var cells = Split(lines[row]);
            if (cells.Length != header.Length)
                throw new FormatException($"Line {row + 1} has {cells.Length} values; the header has {header.Length}.");
            var numbers = cells.Select(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new FormatException($"'{c}' on line {row + 1} is not a number.")).ToArray();
            angles.Add(numbers[0]);
            for (var b = 0; b < 8; b++) levels[b].Add(numbers[bandColumn[b]]);
        }

        try
        {
            return new PolarDirectivity(angles, levels);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    private static string[] Split(string line) => line.Split(',', ';', '\t').Select(c => c.Trim()).ToArray();
}
