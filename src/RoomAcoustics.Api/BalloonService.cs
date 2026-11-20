using System.Collections.Concurrent;
using System.Text;
using RoomAcoustics.Core;

namespace RoomAcoustics.Api;

/// <summary>
/// Keeps the few most recent measured balloons (loudspeaker patterns uploaded as CSV, a CLF text file or a BYU CIF CSV, told apart by what they hold, not by
/// their names) in memory, so a request can refer to one by id. A CLF binary distribution file (.CF2) and an EASE 3 or 4 balloon (.lob) are recognised by their
/// first bytes; any other binary file (a .CF1, say) is refused with what to do instead.
/// </summary>
public static class BalloonService
{
    private const int MaxBalloons = 16;
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly char[] Blanks = { '\uFEFF', ' ', '\t', '\r', '\n' };
    private static readonly ConcurrentDictionary<string, BalloonDirectivity> Store = new();
    private static readonly ConcurrentQueue<string> Order = new();

    public static BalloonDirectivity Get(string id) =>
        Store.TryGetValue(id, out var balloon) ? balloon : throw new ArgumentException("That measured source pattern is no longer loaded; upload it again.");

    public static BalloonDto Import(Stream data, string name)
    {
        using var buffer = new MemoryStream();
        data.CopyTo(buffer);
        if (buffer.Length == 0) throw new ArgumentException("The file is empty.");
        if (buffer.Length > MaxBytes) throw new ArgumentException("The file is larger than 4 MB.");
        var bytes = buffer.ToArray();
        if (ClfReader.IsCf2(bytes))
        {
            try
            {
                var cf2 = ClfReader.ParseCf2(bytes);
                return Keep(cf2.Balloon, string.IsNullOrWhiteSpace(cf2.Manufacturer) ? cf2.Model : $"{cf2.Manufacturer} {cf2.Model}");
            }
            catch (FormatException ex) { throw new ArgumentException($"Could not read '{name}' as a CLF distribution file (.CF2): {ex.Message}"); }
        }
        if (EaseReader.IsEase(bytes))
        {
            try { return Keep(EaseReader.ParseBalloon(bytes, Path.GetFileNameWithoutExtension(name)).Balloon, Path.GetFileNameWithoutExtension(name)); }
            catch (FormatException ex) { throw new ArgumentException($"Could not read '{name}' as an EASE speaker balloon: {ex.Message}"); }
        }
        if (IsBinary(bytes))
            throw new ArgumentException($"'{name}' is a binary file the program does not read. CLF .CF2 files and EASE 3 and 4 balloons (.lob) are read; a CLF .CF1 " +
                "(octave bands) is not: open it in the free CLF Viewer and save it as a CLF text file, then load that. EASE's GLL files need AFMG's own reader. A BYU " +
                "instrument file (.CI1 or .CI2) comes with its \"ASCII format\" .csv in the archive: load that instead.");
        BalloonDirectivity balloon;
        try
        {
            var text = Decode(bytes);
            // a CLF text file, or a CIF balloon as BYU's directivity archive gives it (no header: it starts at its first <BAND>)
            var start = text.TrimStart(Blanks);
            if (start.StartsWith("<CLF", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<BAND>", StringComparison.OrdinalIgnoreCase))
            {
                var clf = ClfReader.Parse(text);
                balloon = clf.Balloon;
                if (clf.Model.Length > 0) name = string.IsNullOrWhiteSpace(clf.Manufacturer) ? clf.Model : $"{clf.Manufacturer} {clf.Model}";
                else name = Path.GetFileNameWithoutExtension(name);
            }
            else balloon = BalloonDirectivity.FromCsv(text);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException)
        {
            throw new ArgumentException($"Could not read '{name}' as a source pattern: {ex.Message}");
        }
        return Keep(balloon, name);
    }

    /// <summary>Keeps a pattern under a new id (forgetting the oldest beyond the last few) and describes it, with its horizontal and vertical cuts.</summary>
    private static BalloonDto Keep(BalloonDirectivity balloon, string name)
    {
        var id = Guid.NewGuid().ToString("N");
        Store[id] = balloon;
        Order.Enqueue(id);
        while (Order.Count > MaxBalloons && Order.TryDequeue(out var old)) Store.TryRemove(old, out _);

        var horizontal = Enumerable.Range(0, 8).Select(b => Cut(balloon, b, -180, 180, a => balloon.LevelDb(b, a, 0))).ToList();
        var vertical = Enumerable.Range(0, 8).Select(b => Cut(balloon, b, -90, 90, e => balloon.LevelDb(b, 0, e))).ToList();
        var all = horizontal.Concat(vertical).SelectMany(c => c.LevelsDb).ToList();
        return new BalloonDto(id, name, balloon.AzimuthsDeg.Count, balloon.ElevationsDeg.Count, Math.Round(all.Min(), 1), Math.Round(all.Max(), 1), horizontal, vertical);
    }

    /// <summary>
    /// Whether the file is binary rather than text: a zero byte, or more than 1 % of control characters other than tab, line feed, carriage return and form feed,
    /// in its first 8 kB. Text patterns (CSV, CLF, CIF) have neither.
    /// </summary>
    private static bool IsBinary(byte[] bytes)
    {
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 8192));
        var control = 0;
        foreach (var b in head)
        {
            if (b == 0) return true;
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0C or 0x0D)) control++;
        }
        return control > head.Length / 100;
    }

    /// <summary>UTF-8 if it is valid UTF-8, otherwise Latin-1 (the encoding of a CLF file written by a Windows spreadsheet).</summary>
    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static PolarCutDto Cut(BalloonDirectivity balloon, int band, int from, int to, Func<double, double> level)
    {
        var angles = Enumerable.Range(0, (to - from) / 5 + 1).Select(i => (double)(from + i * 5)).ToList();
        return new PolarCutDto(OctaveSpectrum.Frequencies[band], angles, angles.Select(a => Math.Round(level(a), 2)).ToList());
    }
}
