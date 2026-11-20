using System.Globalization;
using System.Text;

namespace RoomAcoustics.Core;

/// <summary>
/// Reads the directivity balloon of an EASE 3 or 4 loudspeaker (its .lob file), as manufacturers offer them for download beside the .spk that describes the
/// speaker. The layout is AFMG's and not published; it was read from Genelec's EASE 3 and EASE 4 files and checked against the same speakers' CLF files (to
/// 1 dB RMS over the horizontal and vertical planes, band by band).
/// </summary>
/// <remarks>
/// <para>
/// The file starts "EASE"; the two bytes at 12 are 0 in EASE 3 and 1 in EASE 4, and the four at 18 the number of bands. A name follows at byte 30 (16
/// characters in EASE 3; in EASE 4 a two-byte length, then the characters), then 5 348 bytes, then a record for each band: a name in the same form, 20 bytes,
/// then 72 rows of 37 little-endian two-byte integers, and the file ends with the last. A value is the attenuation in hundredths of a dB,
/// 0 on axis. Row k is at 5k degrees of rotation about the front axis, from the left (the source's +y) through the top; along a row, value j is at 5j
/// degrees from the front, to the back at 180. The bands are the 21 third octaves from 100 Hz to 10 kHz.
/// </para>
/// <para>
/// That the rows turn through the top from the side was found from the speakers' own asymmetry (a two-way speaker is not the same above and below); that
/// they start from the left rather than the right follows from the rotation being right-handed about the front axis, as EASE's frame is, and could not be
/// checked against a speaker that differs left from right. The balloon is read as the CLF2 text file it would be (CLF's arcs turn from the top through the
/// left, so CLF's rotation is 90 degrees less EASE's), and so is joined into octave bands and set in the frame as one.
/// </para>
/// <para>
/// An EASE phase balloon (.phs) has the same records, its values in hundredths of a degree and not 0 on axis, and is refused, as is a file with no balloon (a
/// .spk) or another number of bands. EASE's later GLL files are not read: they need AFMG's own reader.
/// </para>
/// </remarks>
public static class EaseReader
{
    private static readonly byte[] Signature = "EASE"u8.ToArray();
    private const int Rows = 72, Angles = 37, NameAt = 30, AfterFileName = 5348, AfterBandName = 20, Ease3Name = 16;

    private static readonly int[] BandsHz = { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000 };

    /// <summary>Whether the bytes start as an EASE 3 or 4 file does (a .spk, .lob, .phs or .frd).</summary>
    public static bool IsEase(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Signature);

    /// <summary>The balloon of an EASE .lob file, named <paramref name="name"/> (the .lob itself does not name the speaker).</summary>
    public static ClfBalloon ParseBalloon(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!IsEase(bytes)) throw new FormatException("This is not an EASE file.");
        const int size = Rows * Angles * 2;
        if (bytes.Length < 22) throw new FormatException("The EASE file is too short to be anything.");
        if (bytes[10] == 'r' && bytes[11] == 'd')
            throw new FormatException("This EASE file is a frequency response (.frd), not a directivity balloon: load the .lob beside it.");
        var declared = BitConverter.ToInt32(bytes, 18);
        if (declared <= 0)
            throw new FormatException("This EASE file has no directivity balloon: an EASE 3 or 4 speaker's pattern is in its .lob file (beside its .spk); load that.");
        if (declared != BandsHz.Length) throw new FormatException($"The balloon has {declared} bands; the EASE balloons read here have {BandsHz.Length} (100 Hz to 10 kHz).");

        // walk the layout: the names are padded (EASE 3) or length-prefixed (EASE 4); it must end exactly at the end of the file
        var ease4 = BitConverter.ToInt16(bytes, 12) == 1;
        int SkipName(int at) => ease4 ? at + 2 + (at + 2 <= bytes.Length ? BitConverter.ToUInt16(bytes, at) : 0) : at + Ease3Name;
        var records = new List<int>();
        var position = SkipName(NameAt) + AfterFileName;
        for (var band = 0; band < declared && position < bytes.Length; band++)
        {
            position = SkipName(position) + AfterBandName;
            records.Add(position);
            position += size;
        }
        if (records.Count != declared || position != bytes.Length)
            throw new FormatException($"The EASE balloon is laid out differently from the EASE {(ease4 ? 4 : 3)} files this reader knows ({bytes.Length} bytes; its {declared} bands would end at {position}).");

        short Value(int band, int row, int angle) => BitConverter.ToInt16(bytes, records[band] + (row * Angles + angle) * 2);
        for (var band = 0; band < BandsHz.Length; band++)
            for (var row = 0; row < Rows; row++)
                if (Value(band, row, 0) != 0)
                    throw new FormatException("This is an EASE phase balloon (.phs), not levels: its values are not 0 on axis. Load the .lob beside it.");

        var text = new StringBuilder("<CLF2>\n");
        var model = name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (model.Length > 0) text.Append("<MODELNAME>\t").Append(model).Append('\n');
        text.Append("<BALLOON-SYMMETRY>\t<none>\n");
        for (var band = 0; band < BandsHz.Length; band++)
        {
            text.Append("<BAND>\t").Append(BandsHz[band]).Append('\n');
            for (var arc = 0; arc < Rows; arc++)
            {
                var row = ((Rows / 4 - arc) % Rows + Rows) % Rows; // CLF's arc at 5 x arc degrees from the top is EASE's row at 90 - 5 x arc from the left
                for (var angle = 0; angle < Angles; angle++)
                {
                    if (angle > 0) text.Append('\t');
                    text.Append((-Value(band, row, angle) / 100.0).ToString("R", CultureInfo.InvariantCulture));
                }
                text.Append('\n');
            }
        }
        return ClfReader.Parse(text.ToString());
    }
}
