using System.Globalization;
using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The Common Loudspeaker Format's text files (CLF1 at 10 degrees in octave bands, CLF2 at 5 degrees in third-octave bands), read as source patterns.</summary>
public class ClfTextTests
{
    /// <summary>The rotation angles of the arcs, in the order a file lists them, for a symmetry (the CLF spec's table of arcs).</summary>
    private static double[] Arcs(string symmetry, bool reversed, int step)
    {
        double[] Range(int from, int to) => Enumerable.Range(0, (to - from) / step + 1).Select(i => (double)(from + i * step)).ToArray();
        var list = symmetry switch
        {
            "full" => Range(0, 90),
            "vertical" => Range(270, 350).Concat(Range(0, 90)).ToArray(),
            "horizontal" => Range(0, 180),
            "none" => Range(0, 360 - step),
            "rotational" => new[] { 0.0 },
            "polar" => new[] { 0.0, 90, 180, 270 },
            _ => throw new ArgumentException(symmetry),
        };
        if (!reversed) return list;
        // "none" runs 0, 350, 340 ... 10; the others are simply the other way round
        return symmetry == "none" ? list.Select(a => (360 - a) % 360).ToArray() : list.Reverse().ToArray();
    }

    /// <param name="value">The value in the file for a band (Hz), the arc's rotation angle and the angle along the arc, from the front.</param>
    private static string Text(int type, double[] bands, Func<double, double, double, double> value, string symmetry = "horizontal", bool reversedOrder = false,
        string reference = "<relative>\t<SIGN>\t<reversed>", string radiation = "<Fullsphere>", string rotate = "", bool comma = false, string? header = null, string? arcsOverride = null)
    {
        var step = type == 1 ? 10 : 5;
        var columns = radiation.Contains("half", StringComparison.OrdinalIgnoreCase) ? 90 / step + 1 : 180 / step + 1;
        string N(double v) => comma ? v.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',') : v.ToString("0.###", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"<CLF{type}>", "<VERSION>\t1", "<MODELNAME>\tTest model", "<MANUFACTURER>\tTest maker", $"<MINBAND>\t{bands.Min():0}", $"<MAXBAND>\t{bands.Max():0}", "<TYPE>\t<Passive>",
            $"<RADIATION>\t{radiation}", $"<BALLOON-SYMMETRY>\t<{arcsOverride ?? symmetry}>", $"<BALLOON-ARC-ORDER>\t<{(reversedOrder ? "reversed" : "default")}>{(rotate == "" ? "" : "\t<ROTATE>\t" + rotate)}",
            $"<BALLOON-REF>\t{reference}",
        };
        if (header is not null) lines.Add(header);
        foreach (var band in bands)
        {
            lines.Add($"<BAND>\t{band:0}");
            foreach (var arc in Arcs(symmetry, reversedOrder, step))
                lines.Add(string.Join("\t", Enumerable.Range(0, columns).Select(c => N(value(band, arc, c * step)))) + "\t#a comment after the data");
        }
        lines.Add($"<CLF{type}END>");
        return string.Join("\r\n", lines);
    }

    private static readonly double[] Octaves = { 125, 250, 500, 1000, 2000, 4000, 8000 };

    // directions of the source's frame (front +x, left +y, up +z) as azimuth and elevation
    private static double Level(ClfBalloon clf, int band, double azimuth, double elevation) => clf.Balloon.LevelDb(band, azimuth, elevation);

    [Fact]
    public void ArcsRunFromTheTop_ThroughTheLeft_AndTheLevelsAreTheNegativeOfTheValuesWhenTheyAreGivenReversed()
    {
        // the value (dB down) grows with the angle from the front, and more so in the horizontal plane (arcs 90, left, and 270, right) than the vertical (0, top, and 180, bottom)
        double Down(double hz, double arc, double along) => 0.1 * along + 0.1 * along * Math.Pow(Math.Sin(arc * Math.PI / 180), 2);
        var clf = ClfReader.Parse(Text(1, Octaves, Down, "none"));
        Assert.Equal("Test model", clf.Model);
        Assert.Equal("Test maker", clf.Manufacturer);
        Assert.Equal(0, Level(clf, 3, 0, 0), 6);                       // on axis
        Assert.Equal(-8, Level(clf, 3, 40, 0), 6);                     // 40 degrees to the left: along the arc at 90, two tenths of a dB a degree
        Assert.Equal(-8, Level(clf, 3, -40, 0), 6);                    // and to the right: along the arc at 270
        Assert.Equal(-4, Level(clf, 3, 0, 40), 6);                     // 40 degrees up: along the arc at 0, a tenth of a dB a degree
        Assert.Equal(-4, Level(clf, 3, 0, -40), 6);                    // down: along the arc at 180
        Assert.Equal(-18, Level(clf, 3, 180, 0), 6);                   // behind
    }

    [Fact]
    public void TheLeftAndTheRightCanDiffer_AndTheLeftIsWhereTheArcAt90Points()
    {
        double Down(double hz, double arc, double along) => arc == 90 ? 0.2 * along : arc == 270 ? 0.05 * along : 0.1 * along;
        var clf = ClfReader.Parse(Text(1, Octaves, Down, "none"));
        Assert.Equal(-8, Level(clf, 3, 40, 0), 6);   // left
        Assert.Equal(-2, Level(clf, 3, -40, 0), 6);  // right
    }

    [Theory]
    [InlineData("full")]
    [InlineData("vertical")]
    [InlineData("horizontal")]
    [InlineData("rotational")]
    public void ASymmetricBalloonIsFilledInAllRound(string symmetry)
    {
        // a pattern with the symmetry of the file: it does not depend on the side, or on above and below
        double Down(double hz, double arc, double along) => 0.1 * along + (symmetry == "rotational" ? 0 : 0.1 * along * Math.Pow(Math.Sin(arc * Math.PI / 180), 2));
        var clf = ClfReader.Parse(Text(1, Octaves, Down, symmetry));
        var sideways = symmetry == "rotational" ? -4 : -8;
        Assert.Equal(sideways, Level(clf, 3, 40, 0), 6);
        Assert.Equal(sideways, Level(clf, 3, -40, 0), 6);
        Assert.Equal(-4, Level(clf, 3, 0, 40), 6);
        Assert.Equal(-4, Level(clf, 3, 0, -40), 6);
    }

    [Fact]
    public void AFileCanListItsArcsTheOtherWayRound()
    {
        double Down(double hz, double arc, double along) => arc == 180 ? 0.2 * along : 0.1 * along;
        foreach (var symmetry in new[] { "vertical", "horizontal", "none" })
        {
            var forward = ClfReader.Parse(Text(1, Octaves, Down, symmetry == "vertical" ? "horizontal" : symmetry));
            var back = ClfReader.Parse(Text(1, Octaves, Down, symmetry == "vertical" ? "horizontal" : symmetry, reversedOrder: true).Replace("<default>", "<reversed>"));
            Assert.Equal(Level(forward, 3, 40, 10), Level(back, 3, 40, 10), 6);
        }
    }

    [Fact]
    public void RotateTurnsTheBalloonTowardsTheLeft()
    {
        // the arc at 0 (the top) is the only one with a steep fall; turned by 90 degrees in the arc order, it is the one that points left
        double Down(double hz, double arc, double along) => arc == 0 ? 0.3 * along : 0.1 * along;
        var plain = ClfReader.Parse(Text(1, Octaves, Down, "none"));
        var turned = ClfReader.Parse(Text(1, Octaves, Down, "none", rotate: "90"));
        Assert.Equal(-12, Level(plain, 3, 0, 40), 6);
        Assert.Equal(-12, Level(turned, 3, 40, 0), 6);
        Assert.Equal(-4, Level(turned, 3, 0, 40), 6);
    }

    [Fact]
    public void LevelsGivenAsTheyAre_AreNotFlipped_AndTheDecimalCommaIsUnderstood()
    {
        double Actual(double hz, double arc, double along) => -0.1 * along;
        var clf = ClfReader.Parse(Text(1, Octaves, Actual, "none", reference: "<relative>\t<SIGN>\t<actual>", comma: true));
        Assert.Equal(-4, Level(clf, 3, 40, 0), 6);
        var absolute = ClfReader.Parse(Text(1, Octaves, (hz, arc, along) => 90 - 0.1 * along, "none", reference: "<absolute>"));
        Assert.Equal(-4, Level(absolute, 3, 40, 0) - Level(absolute, 3, 0, 0), 6);
    }

    [Fact]
    public void BandsOutsideTheRangeTakeTheNearestBandGiven()
    {
        double Down(double hz, double arc, double along) => along / 100 * Math.Log2(hz / 100);
        var clf = ClfReader.Parse(Text(1, new double[] { 250, 500, 1000, 2000, 4000 }, Down, "none"));
        Assert.Equal(Level(clf, 2, 40, 0), Level(clf, 0, 40, 0), 9);   // 63 Hz is the 250 Hz data
        Assert.Equal(Level(clf, 2, 40, 0), Level(clf, 1, 40, 0), 9);   // 125 Hz too
        Assert.Equal(Level(clf, 6, 40, 0), Level(clf, 7, 40, 0), 9);   // 8 kHz is the 4 kHz data
        Assert.NotEqual(Level(clf, 2, 40, 0), Level(clf, 4, 40, 0));
    }

    [Fact]
    public void ThirdOctaveBandsAreJoinedIntoOctaveBandsByEnergy()
    {
        var thirds = new double[] { 100, 125, 160, 800, 1000, 1250 };
        // 100, 125 and 160 Hz make the 125 Hz octave; 800, 1000 and 1250 Hz the 1 kHz octave. The levels are 0, -3 and -6 dB down at 40 degrees in the first, flat in the second.
        double Down(double hz, double arc, double along) => along == 40 && hz < 200 ? new Dictionary<double, double> { [100] = 0, [125] = 3, [160] = 6 }[hz] : 0;
        var clf = ClfReader.Parse(Text(2, thirds, Down, "none", reference: "<relative>\t<SIGN>\t<reversed>"));
        var expected = 10 * Math.Log10((1 + Math.Pow(10, -0.3) + Math.Pow(10, -0.6)) / 3);
        Assert.Equal(expected, Level(clf, 1, 40, 0), 6);
        Assert.Equal(0, Level(clf, 4, 40, 0), 6);
    }

    [Fact]
    public void AHalfSphereSpeakerRadiatesNothingBehindItsBaffle()
    {
        double Down(double hz, double arc, double along) => 0.05 * along;
        var clf = ClfReader.Parse(Text(1, Octaves, Down, "full", radiation: "<Halfsphere>"));
        Assert.Equal(-2, Level(clf, 3, 40, 0), 6);
        Assert.True(Level(clf, 3, 180, 0) < Level(clf, 3, 90, 0) - 15, "well below the level at the edge of the baffle");
    }

    [Fact]
    public void ThePolarSymmetryInterpolatesBetweenTheFourArcs()
    {
        double Down(double hz, double arc, double along) => arc is 0 or 180 ? 0.2 * along : 0.1 * along;
        var clf = ClfReader.Parse(Text(1, Octaves, Down, "polar"));
        Assert.Equal(-4, Level(clf, 3, 40, 0), 6);
        Assert.Equal(-8, Level(clf, 3, 0, 40), 6);
        // 40 degrees from the front half way round from the top to the left: between the 0.2 and 0.1 dB a degree of the two arcs
        double θ = 40 * Math.PI / 180, φ = 45 * Math.PI / 180;
        var azimuth = Math.Atan2(Math.Sin(θ) * Math.Sin(φ), Math.Cos(θ)) * 180 / Math.PI;
        var elevation = Math.Asin(Math.Sin(θ) * Math.Cos(φ)) * 180 / Math.PI;
        Assert.InRange(Level(clf, 3, azimuth, elevation), -7, -5);
    }

    [Theory]
    [InlineData("<CLF3>", "<CLF2>")]
    [InlineData("<BALLOON-SYMMETRY>\t<diagonal>", "diagonal")]
    public void AFileThatIsNotUnderstoodIsExplained(string replacement, string expectedInMessage)
    {
        var text = Text(1, Octaves, (hz, arc, along) => 0, "horizontal");
        text = replacement.StartsWith("<CLF") ? text.Replace("<CLF1>", replacement) : text.Replace("<BALLOON-SYMMETRY>\t<horizontal>", replacement);
        var error = Assert.Throws<FormatException>(() => ClfReader.Parse(text));
        Assert.Contains(expectedInMessage, error.Message);
    }

    [Fact]
    public void TheWrongNumberOfArcsOrAnglesIsReportedWithTheBand()
    {
        var text = Text(1, Octaves, (hz, arc, along) => 0, "horizontal");
        var short1 = text.Replace("<BALLOON-SYMMETRY>\t<horizontal>", "<BALLOON-SYMMETRY>\t<none>");   // 36 arcs expected, 19 given
        var error = Assert.Throws<FormatException>(() => ClfReader.Parse(short1));
        Assert.Contains("125", error.Message);
        Assert.Contains("36", error.Message);

        var noBalloon = string.Join("\r\n", text.Split("\r\n").TakeWhile(l => !l.StartsWith("<BAND>")).Append("<CLF1END>"));
        Assert.Contains("no balloon", Assert.Throws<FormatException>(() => ClfReader.Parse(noBalloon)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASecondPartOfAMultiPartFileIsNamedAsOne()
    {
        var text = Text(2, new double[] { 100, 125, 160 }, (hz, arc, along) => 0, "horizontal", header: "<PART>\t2\t<OF>\t3\t<INFO>\tHigh frequency driver");
        var clf = ClfReader.Parse(text);
        Assert.Contains("part 2 of 3", clf.Model);
    }

    /// <summary>
    /// A CIF balloon as BYU's directivity archive gives it beside each .CI2 file ("Flute.csv"): no header, each third-octave band a <c>&lt;BAND&gt;</c> line (a TAB
    /// before the frequency) and then 72 arcs of 37 levels, comma separated with a comma at the end of each line, CRLF line ends.
    /// </summary>
    private static string ByuCif(int[] bands, Func<double, double, double> level) =>
        string.Concat(bands.Select(hz => $"<BAND>\t{hz}\r\n" + string.Concat(Enumerable.Range(0, 72).Select(arc =>
            string.Concat(Enumerable.Range(0, 37).Select(k => level(arc * 5, k * 5).ToString("0.00", CultureInfo.InvariantCulture) + ",")) + "\r\n"))));

    private static readonly int[] FluteBands = { 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000 };

    [Fact]
    public void ACifBalloonFromByusArchive_IsRead_WithItsArcsRunningToTheRight()
    {
        // 5 dB per unit towards the front and 2 dB towards the arc at 90 degrees, which in BYU's files is the source's right (their flute is loudest there, as
        // a flute is, its open end to the player's right): the arc at rotation r runs through (cos a, -sin a sin r, sin a cos r)
        double Shape(double arc, double along) =>
            5 * Math.Cos(along * Math.PI / 180) + 2 * Math.Sin(along * Math.PI / 180) * Math.Sin(arc * Math.PI / 180);
        var clf = ClfReader.Parse(ByuCif(FluteBands, Shape));

        Assert.Equal("", clf.Model); // no name in the file: the upload names it after the file
        for (var band = 0; band < 8; band++) // every octave band, those the file does not reach (63, 125 and 8 kHz) taking the nearest it does
        {
            Assert.Equal(10, Level(clf, band, 0, 0) - Level(clf, band, 180, 0), 1);
            Assert.Equal(4, Level(clf, band, -90, 0) - Level(clf, band, 90, 0), 1);
            Assert.Equal(0, Level(clf, band, 0, 90) - Level(clf, band, 0, -90), 1); // nothing between up and down
        }
    }

    [Fact]
    public void ACifBalloonWithTheWrongNumberOfArcs_IsExplained()
    {
        var text = ByuCif(FluteBands, (_, _) => 0);
        var cut = string.Join("\r\n", text.Split("\r\n").Where((_, i) => i != 5)); // one arc of the 250 Hz band left out
        var error = Assert.Throws<FormatException>(() => ClfReader.Parse(cut));
        Assert.Contains("250 Hz band has 71 arcs", error.Message);
    }
}
