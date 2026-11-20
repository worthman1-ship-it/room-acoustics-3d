using System.Globalization;

namespace RoomAcoustics.Core;

/// <summary>A loudspeaker read from a CLF text file: what it is called, and its directivity balloon in the eight octave bands.</summary>
public sealed record ClfBalloon(string Model, string? Manufacturer, string? Description, BalloonDirectivity Balloon);

/// <summary>
/// Reads the Common Loudspeaker Format's text files (the CLF Group's "CLF text format", version 1 and 2, TAB separated): CLF1, balloons at 10 degrees in octave
/// bands, and CLF2, at 5 degrees in third-octave bands. Only the loudspeaker's directivity is used; its sensitivity, impedance, cabinet drawing, phase and filter data
/// are skipped. The binary .CF2 distribution file is read too (<see cref="ParseCf2"/>); the binary .CF1 is not (the CLF viewer saves a text file from one).
/// </summary>
/// <remarks>
/// <para>
/// It also reads the bare balloons of the Common Instrument Format (CIF, the CLF Group's format for talkers, instruments and other natural sources) that BYU's
/// directivity archive gives beside each .CI2 file as "ASCII format": no header, a <c>&lt;BAND&gt;</c> line for each third-octave band, then the whole sphere as 72
/// arcs of 37 levels, comma separated, every 5 degrees. They are read as a CLF2 balloon with no symmetry, the levels as given (dB, 0 down) and no model name.
/// The frame is CLF's: the archive "realigned the frontal axis at theta = 0", with the original zenith at (90, 0) and the nadir at (90, 180), so the front is
/// 0 degrees along every arc, the arc at 0 passes through the top and the one at 180 through the bottom. Which way the arcs run is not said, and they run the
/// other way from CLF's: the arc at 90 is the source's right. The archive's flute, read that way, is loudest to the player's right from 630 Hz up, as Pätynen
/// and Lokki (Acta Acustica 96, 2010) measured a flute (its open end and tone holes are to the right); read CLF's way it would be a mirror image.
/// </para>
/// <para>
/// A balloon is given as arcs: half great circles from the front (0 degrees along the arc) round to the back (180), one for each rotation angle about the axis.
/// The spec's Fig. 1 numbers them anticlockwise as seen from behind the speaker, from the top (0) through the left (90) and the bottom (180) to the right (270),
/// and the cabinet frame is front +x, left +y, up +z, so the direction at angle <c>a</c> along the arc at rotation <c>r</c> is
/// (cos a, sin a sin r, sin a cos r): the frame of <see cref="BalloonDirectivity"/>, whose azimuth runs from the front to the left. So the "horizontal"
/// symmetry (arcs 0 to 180, the left half) is a mirror between left and right, and "vertical" (270 to 90, the top half) one between top and bottom.
/// </para>
/// <para>
/// The balloon's symmetry says which arcs the file lists, and the others are filled in by mirroring (for the "polar" symmetry, four arcs, by interpolating between
/// them in dB). Third-octave bands are joined into octave bands by energy, and an octave band outside the file's range takes the nearest band the file gives. A
/// half-sphere speaker (a ceiling speaker, meant for a baffle) is given only to 90 degrees; behind it the level is set 30 dB below its level at 90 degrees.
/// Levels are relative: with <c>&lt;reversed&gt;</c> sign a value of 3 means 3 dB down; the source's power is normalised afterwards, so only their shape matters.
/// </para>
/// </remarks>
public static class ClfReader
{
    private const double BehindTheBaffleDb = 30;

    // the octave band (index into OctaveSpectrum.Frequencies) of each standard band; 1/1 octaves in CLF1, 1/3 octaves in CLF2; bands above 8 kHz are not used
    private static readonly Dictionary<int, int> OctaveOf = new() { [63] = 0, [125] = 1, [250] = 2, [500] = 3, [1000] = 4, [2000] = 5, [4000] = 6, [8000] = 7, [16000] = -1 };
    private static readonly Dictionary<int, int> ThirdOctaveOf = new()
    {
        [50] = 0, [63] = 0, [80] = 0, [100] = 1, [125] = 1, [160] = 1, [200] = 2, [250] = 2, [315] = 2, [400] = 3, [500] = 3, [630] = 3, [800] = 4, [1000] = 4, [1250] = 4,
        [1600] = 5, [2000] = 5, [2500] = 5, [3150] = 6, [4000] = 6, [5000] = 6, [6300] = 7, [8000] = 7, [10000] = 7, [12500] = -1, [16000] = -1, [20000] = -1,
    };

    private sealed class Block
    {
        public int Hz;
        public int Line;
        public List<double[]> Rows { get; } = new();
    }

    /// <summary>The first bytes of every CF2 file seen (the CLF authoring tool's versions 1.0h and 2.1a).</summary>
    private static readonly byte[] Cf2Signature = { 0x41, 0xBD, 0x0A, 0x00 };

    /// <summary>Where a CF2 keeps its balloon, and its shape: 23 third-octave bands (100 Hz to 16 kHz) of 72 arcs of 37 angles, 4-byte floats.</summary>
    private const int Cf2BalloonAt = 78_172, Cf2Bands = 23, Cf2Arcs = 72, Cf2Angles = 37;

    private static readonly int[] Cf2BandsHz = { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000 };

    /// <summary>Whether the bytes start as a CLF binary distribution file (.CF2) does.</summary>
    public static bool IsCf2(ReadOnlySpan<byte> bytes) => bytes.Length >= Cf2Signature.Length && bytes[..Cf2Signature.Length].SequenceEqual(Cf2Signature);

    /// <summary>
    /// Reads a CLF binary distribution file for a third-octave loudspeaker (.CF2). Its layout is not published; it was read from Genelec's CF2 files (the
    /// authoring tool's versions 1.0h and 2.1a, all 334 040 bytes) and checked against JBL's (versions 2.0a and 2.1b, the same 334 040 bytes with more after
    /// them): text fields of 256 bytes (the manufacturer at 568, the model at 312, the description at 824),
    /// then at byte 78 172 the balloon as little-endian 4-byte floats, band by band, arc by arc (every 5 degrees of rotation, from the top as in the text
    /// format), angle by angle along the arc (every 5 degrees from the front): the level in dB relative to on axis, every arc given. The point straight behind
    /// is stored as 0 on every arc, so it is taken as the energy mean of the ring 5 degrees from it. A file measured only up to some band (JBL's to 10 kHz) has
    /// junk in the bands above, and is read to the last band it has. The balloon is then read as the CLF2 text file it would be,
    /// so it is joined into octave bands and set in the frame as a text file is. A file with the signature but not this layout is refused: too short, no
    /// first band, or a band whose level on axis is not near 0 dB on any arc.
    /// </summary>
    public static ClfBalloon ParseCf2(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!IsCf2(bytes)) throw new FormatException("This is not a CLF binary distribution file (.CF2).");
        var end = Cf2BalloonAt + Cf2Bands * Cf2Arcs * Cf2Angles * 4;
        if (bytes.Length < end) throw new FormatException($"The CF2 file is too short ({bytes.Length} bytes) to hold a balloon where CF2 files keep one (to byte {end}): it may be a CF1, which is not read.");

        // bands are read from the lowest up; a file measured to fewer bands leaves the rest as whatever was in memory, so the first band that is not a
        // balloon (a level that is not finite or beyond 200 dB) ends it. The levels are re on axis, so on axis is near 0 dB, though not on every arc in every
        // file (JBL's CBT 70J has -10 dB on some arcs at 16 kHz), and off axis may be louder (its Control 62P, 6.5 dB behind at 250 Hz).
        var levels = new float[Cf2Bands, Cf2Arcs, Cf2Angles];
        var bands = 0;
        for (; bands < Cf2Bands; bands++)
        {
            var measured = true;
            for (var arc = 0; arc < Cf2Arcs && measured; arc++)
                for (var angle = 0; angle < Cf2Angles && measured; angle++)
                {
                    var v = BitConverter.ToSingle(bytes, Cf2BalloonAt + ((bands * Cf2Arcs + arc) * Cf2Angles + angle) * 4);
                    measured = float.IsFinite(v) && Math.Abs(v) <= 200;
                    levels[bands, arc, angle] = v;
                }
            if (!measured) break;
        }
        if (bands == 0) throw new FormatException("The CF2 file has no balloon where CF2 files keep one: it is laid out differently from the CF2 files this reader knows.");
        for (var band = 0; band < bands; band++)
        {
            var onAxis = Enumerable.Range(0, Cf2Arcs).Max(arc => levels[band, arc, 0]);
            if (Math.Abs(onAxis) > 6)
                throw new FormatException($"The CF2 file's level on axis is not near 0 dB on any arc ({onAxis:0.#} dB at {Cf2BandsHz[band]} Hz): it is laid out differently from the CF2 files this reader knows.");
        }

        string Field(int at) => System.Text.Encoding.Latin1.GetString(bytes, at, 256).Split('\0')[0].Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        var text = new System.Text.StringBuilder("<CLF2>\n");
        foreach (var (label, at) in new[] { ("MANUFACTURER", 568), ("MODELNAME", 312), ("DESCRIPTION", 824) })
            if (Field(at) is { Length: > 0 } value) text.Append('<').Append(label).Append(">\t").Append(value).Append('\n');
        text.Append("<BALLOON-SYMMETRY>\t<none>\n");
        for (var band = 0; band < bands; band++)
        {
            text.Append("<BAND>\t").Append(Cf2BandsHz[band]).Append('\n');
            var behind = 10 * Math.Log10(Enumerable.Range(0, Cf2Arcs).Average(arc => Math.Pow(10, levels[band, arc, Cf2Angles - 2] / 10.0)));
            for (var arc = 0; arc < Cf2Arcs; arc++)
            {
                for (var angle = 0; angle < Cf2Angles; angle++)
                {
                    if (angle > 0) text.Append('\t');
                    text.Append((angle == Cf2Angles - 1 ? behind : levels[band, arc, angle]).ToString("R", CultureInfo.InvariantCulture));
                }
                text.Append('\n');
            }
        }
        return Parse(text.ToString());
    }

    public static ClfBalloon Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var header = new Dictionary<string, string[]>();
        var blocks = new List<Block>();
        Block? current = null; // the band whose magnitude arcs are being read; phase arcs and anything after the balloon are skipped
        var type = 0;

        var lines = text.Split('\n');
        var bare = false; // a CIF balloon as BYU's archive gives it: no header, the arcs comma separated
        for (var n = 0; n < lines.Length; n++)
        {
            var cells = lines[n].TrimEnd('\r').Split(bare && !lines[n].TrimStart().StartsWith('<') ? ',' : '\t').Select(c => c.Trim()).ToArray();
            if (cells[0].Length == 0 || cells[0][0] == '#') continue;
            var isLabel = cells[0][0] == '<' && cells[0][^1] == '>';

            if (type == 0)
            {
                type = isLabel && cells[0].Equals("<CLF1>", StringComparison.OrdinalIgnoreCase) ? 1 : isLabel && cells[0].Equals("<CLF2>", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                if (type == 0 && isLabel && cells[0].Equals("<BAND>", StringComparison.OrdinalIgnoreCase))
                {
                    (type, bare) = (2, true);
                    header["BALLOON-SYMMETRY"] = new[] { "<none>" };
                    header["BALLOON-ARC-ORDER"] = new[] { "<reversed>" }; // the arcs run from the top to the right (see the remarks)
                    n--; // the <BAND> line is read again, as the first band
                    continue;
                }
                if (type == 0) throw new FormatException($"Line {n + 1}: the first line of a CLF text file must be <CLF1> or <CLF2>, so this is not a CLF text file (the binary .CF1 and .CF2 files are not read: save a text file from the CLF viewer).");
                continue;
            }
            if (!isLabel)
            {
                if (current is not null) current.Rows.Add(NumbersOf(cells, n + 1));
                continue;
            }

            var label = cells[0].Trim('<', '>').ToUpperInvariant();
            if (label is "CLF1END" or "CLF2END") break;
            var values = cells.Skip(1).TakeWhile(c => c.Length == 0 || c[0] != '#').ToArray();
            if (label == "BAND")
            {
                if (values.Length == 0 || !double.TryParse(values[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
                    throw new FormatException($"Line {n + 1}: <BAND> must be followed by a centre frequency.");
                current = new Block { Hz = (int)Math.Round(hz), Line = n + 1 };
                blocks.Add(current);
            }
            else if (label == "PHASE") current = null;
            else
            {
                current = null; // an ordinary field: any numbers on the lines after it (cabinet edges, say) are not balloon arcs
                header[label] = values;
            }
        }
        if (type == 0) throw new FormatException("The file is empty: a CLF text file starts with <CLF1> or <CLF2>.");
        if (blocks.Count == 0) throw new FormatException("The file has no balloon data: there is no <BAND> block of arcs.");

        var step = type == 1 ? 10 : 5;
        var symmetry = Word(header, "BALLOON-SYMMETRY") ?? throw new FormatException("The file does not say its balloon's symmetry (<BALLOON-SYMMETRY>).");
        var expected = symmetry switch
        {
            "full" => 90 / step + 1, "vertical" or "horizontal" => 180 / step + 1, "none" => 360 / step, "rotational" => 1, "polar" => 4,
            _ => throw new FormatException($"The balloon symmetry '{symmetry}' is not one of full, vertical, horizontal, none, rotational or polar."),
        };
        var reversedOrder = Word(header, "BALLOON-ARC-ORDER") == "reversed";
        var rotate = header.TryGetValue("BALLOON-ARC-ORDER", out var order) && IndexOfWord(order, "rotate") is { } at && at + 1 < order.Length
            ? Number(order[at + 1], "<ROTATE>") : 0;
        if (Math.Abs(rotate / step - Math.Round(rotate / step)) > 1e-9) throw new FormatException($"The balloon rotation {rotate:0.##} degrees is not a multiple of the {step} degree step.");
        var reference = header.TryGetValue("BALLOON-REF", out var refs) ? refs.Select(Strip).ToArray() : Array.Empty<string>();
        var sign = reference.Contains("reversed") ? -1.0 : 1.0;
        var halfSphere = Word(header, "RADIATION") == "halfsphere";
        var columns = (halfSphere ? 90 : 180) / step + 1;
        var allColumns = 180 / step + 1;
        var arcs = 360 / step;

        // each band's table of levels [arc][angle along it], by octave band
        var tables = Enumerable.Range(0, 8).Select(_ => new List<double[][]>()).ToArray();
        var seen = new HashSet<int>();
        foreach (var block in blocks)
        {
            var map = type == 1 ? OctaveOf : ThirdOctaveOf;
            if (!map.TryGetValue(block.Hz, out var octave))
                throw new FormatException($"Line {block.Line}: {block.Hz} Hz is not one of the standard {(type == 1 ? "octave" : "third-octave")} bands of a CLF{type} file.");
            if (!seen.Add(block.Hz)) throw new FormatException($"Line {block.Line}: the {block.Hz} Hz band is given twice.");
            if (octave < 0) continue;
            if (block.Rows.Count != expected)
                throw new FormatException($"The {block.Hz} Hz band has {block.Rows.Count} arcs; a balloon with {symmetry} symmetry has {expected}{(symmetry == "none" ? " (36 for 10 degree steps, 72 for 5)" : "")}.");
            foreach (var row in block.Rows)
                if (row.Length < columns) throw new FormatException($"The {block.Hz} Hz band has an arc of {row.Length} angles; there must be {columns} (every {step} degrees from the front{(halfSphere ? " to 90 degrees" : " to the back")}).");

            var full = Expand(block.Rows.Select(r => r.Take(columns).Select(v => sign * v).ToArray()).ToList(), symmetry, reversedOrder, step, arcs, columns, allColumns);
            var turned = new double[arcs][];
            var turn = (int)Math.Round(rotate / step);
            for (var i = 0; i < arcs; i++) turned[(((i + turn) % arcs) + arcs) % arcs] = full[i];
            tables[octave].Add(turned);
        }
        if (tables.All(t => t.Count == 0)) throw new FormatException("The file has no balloon data within the octave bands 63 Hz to 8 kHz.");

        // an octave band is the energy mean of its thirds; one the file does not reach is the nearest one it does
        var bands = new double[8][][];
        for (var b = 0; b < 8; b++)
        {
            var source = Enumerable.Range(0, 8).Where(i => tables[i].Count > 0).OrderBy(i => Math.Abs(i - b)).ThenBy(i => i).First();
            bands[b] = Join(tables[source], arcs, allColumns);
        }

        var azimuths = Enumerable.Range(0, 72).Select(i => i * 5.0).ToArray();
        var elevations = Enumerable.Range(0, 37).Select(i => -90.0 + i * 5).ToArray();
        var grid = bands.Select(table => elevations.Select(el => azimuths.Select(az => Sample(table, az, el, step)).ToArray()).ToArray()).ToList();

        var model = Text(header, "MODELNAME") ?? (bare ? "" : "CLF loudspeaker");
        if (header.TryGetValue("PART", out var part) && part.Length >= 3 && part[2] != "1") model += $" (part {part[0]} of {part[2]})";
        return new ClfBalloon(model, Text(header, "MANUFACTURER"), Text(header, "DESCRIPTION"), new BalloonDirectivity(azimuths, elevations, grid));
    }

    private static double[] NumbersOf(string[] cells, int line)
    {
        var numbers = new List<double>();
        foreach (var cell in cells)
        {
            if (cell.Equals("<nodata>", StringComparison.OrdinalIgnoreCase)) throw new FormatException($"Line {line}: balloon data must be complete, but this line has <nodata>.");
            if (!double.TryParse(cell.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) break; // a comment after the data
            numbers.Add(v);
        }
        return numbers.ToArray();
    }

    private static string Strip(string value) => value.Trim().Trim('<', '>', '\'', '"').ToLowerInvariant();
    private static string? Word(Dictionary<string, string[]> header, string label) => header.TryGetValue(label, out var v) && v.Length > 0 ? Strip(v[0]) : null;
    private static string? Text(Dictionary<string, string[]> header, string label) => header.TryGetValue(label, out var v) && v.Length > 0 && v[0].Length > 0 ? v[0].Trim('\'', '"') : null;
    private static int? IndexOfWord(string[] values, string word) { var i = Array.FindIndex(values, v => Strip(v) == word); return i < 0 ? null : i; }
    private static double Number(string cell, string what) =>
        double.TryParse(cell.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"{what} must be followed by a number, not '{cell}'.");

    /// <summary>The rotation angles of the arcs a file lists, in the order it lists them.</summary>
    private static double[] ArcAngles(string symmetry, bool reversed, int step)
    {
        double[] Range(int from, int to) => Enumerable.Range(0, (to - from) / step + 1).Select(i => (double)(from + i * step)).ToArray();
        var list = symmetry switch
        {
            "full" => Range(0, 90),
            "vertical" => Range(270, 350).Concat(Range(0, 90)).ToArray(),
            "horizontal" => Range(0, 180),
            "none" => Range(0, 360 - step),
            "rotational" => new[] { 0.0 },
            _ => new[] { 0.0, 90, 180, 270 },
        };
        if (!reversed) return list;
        return symmetry == "none" ? list.Select(a => (360 - a) % 360).ToArray() : list.Reverse().ToArray(); // "none" runs 0, 350, 340 ... 10; the rest are simply the other way round
    }

    /// <summary>The whole sphere, every arc and every angle along it, from the arcs a file gives and its symmetry.</summary>
    private static double[][] Expand(List<double[]> rows, string symmetry, bool reversed, int step, int arcs, int columns, int allColumns)
    {
        var angles = ArcAngles(symmetry, reversed, step);
        var table = new double[arcs][];
        int At(double angle) => (((int)Math.Round(angle / step) % arcs) + arcs) % arcs;
        void Put(double angle, double[] row) => table[At(angle)] ??= row;
        for (var i = 0; i < rows.Count; i++)
        {
            var a = angles[i];
            Put(a, rows[i]);
            switch (symmetry)
            {
                case "full": Put(180 - a, rows[i]); Put(180 + a, rows[i]); Put(360 - a, rows[i]); break;
                case "vertical": Put(180 - a, rows[i]); break;
                case "horizontal": Put(360 - a, rows[i]); break;
                case "rotational": for (var k = 0; k < arcs; k++) Put(k * step, rows[i]); break;
            }
        }
        if (symmetry == "polar")
        {
            var given = Enumerable.Range(0, 4).Select(k => table[At(k * 90)]).ToArray();
            for (var i = 0; i < arcs; i++)
            {
                if (table[i] is not null) continue;
                var phi = i * step;
                var lower = (int)(phi / 90.0); var t = (phi - lower * 90) / 90.0;
                var from = given[lower]; var to = given[(lower + 1) % 4];
                table[i] = Enumerable.Range(0, columns).Select(c => from[c] + t * (to[c] - from[c])).ToArray();
            }
        }
        if (table.Any(r => r is null)) throw new FormatException("The balloon's arcs do not cover the whole sphere."); // cannot happen for a valid symmetry
        if (columns == allColumns) return table;
        // a half sphere: nothing radiates behind the baffle
        return table.Select(row => row.Concat(Enumerable.Repeat(row[^1] - BehindTheBaffleDb, allColumns - columns)).ToArray()).ToArray();
    }

    /// <summary>The energy mean of tables of levels (dB) of the same shape.</summary>
    private static double[][] Join(List<double[][]> tables, int arcs, int columns)
    {
        if (tables.Count == 1) return tables[0];
        return Enumerable.Range(0, arcs).Select(a => Enumerable.Range(0, columns).Select(c =>
            10 * Math.Log10(tables.Average(t => Math.Pow(10, t[a][c] / 10)))).ToArray()).ToArray();
    }

    /// <summary>The level in a direction of the source's frame, bilinear in the arc's rotation and the angle along it.</summary>
    private static double Sample(double[][] table, double azimuthDeg, double elevationDeg, int step)
    {
        double az = azimuthDeg * Math.PI / 180, el = elevationDeg * Math.PI / 180;
        double x = Math.Cos(el) * Math.Cos(az), y = Math.Cos(el) * Math.Sin(az), z = Math.Sin(el);
        var along = Math.Acos(Math.Clamp(x, -1, 1)) * 180 / Math.PI;
        // from the top, anticlockwise seen from behind; at the front and back every arc is the same point, so it does not matter which
        var rotation = along < 1e-6 || along > 180 - 1e-6 ? 0 : Math.Atan2(y, z) * 180 / Math.PI;
        if (rotation < 0) rotation += 360;

        var arcs = table.Length;
        var fa = rotation / step; var a0 = (int)Math.Floor(fa) % arcs; var ta = fa - Math.Floor(fa);
        var a1 = (a0 + 1) % arcs;
        var columns = table[0].Length;
        var fc = Math.Min(along / step, columns - 1); var c0 = Math.Min((int)Math.Floor(fc), columns - 2); var tc = fc - c0;
        double Row(int arc) => table[arc][c0] + tc * (table[arc][c0 + 1] - table[arc][c0]);
        return Row(a0) + ta * (Row(a1) - Row(a0));
    }
}
