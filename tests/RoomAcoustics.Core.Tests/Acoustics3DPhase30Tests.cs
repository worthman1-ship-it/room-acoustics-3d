using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class BalloonDirectivityTests
{
    private static readonly double[] Azimuths = { 0, 90, 180, 270 };
    private static readonly double[] Elevations = { -90, 0, 90 };

    /// <summary>The same grid of levels in every band: loud in front, quieter behind, quieter still to the right; 6 dB down at the poles.</summary>
    private static BalloonDirectivity Speaker()
    {
        var plan = new[] { 0.0, -4, -12, -8 };
        var rows = Elevations.Select(el => plan.Select(v => el == 0 ? v : v - 6).ToArray()).ToArray();
        return new BalloonDirectivity(Azimuths, Elevations, Enumerable.Repeat(rows, 8).ToArray());
    }

    private static double MeanGain(SourceDirectivity d)
    {
        const int n = 4000;
        var sum = 0.0;
        var golden = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < n; i++)
        {
            var z = 1 - 2 * (i + 0.5) / n;
            var r = Math.Sqrt(1 - z * z);
            sum += d.Gain(new Vec3(r * Math.Cos(i * golden), r * Math.Sin(i * golden), z));
        }
        return sum / n;
    }

    [Fact]
    public void TheGridPointsAreReproduced_AndTheLevelsBetweenThemAreInterpolatedInDecibels()
    {
        var b = Speaker();
        Assert.Equal(-4, b.LevelDb(3, 90, 0), 9);
        Assert.Equal(-12, b.LevelDb(3, 180, 0), 9);
        Assert.Equal(-14, b.LevelDb(3, 270, 90), 9);
        Assert.Equal(-8, b.LevelDb(3, 135, 0), 9);                  // halfway between -4 and -12
        Assert.Equal(-3, b.LevelDb(3, 0, 45), 9);                   // halfway between 0 and -6
    }

    [Fact]
    public void AzimuthWrapsRoundTheBack_SoTheLastColumnJoinsTheFirst()
    {
        var b = Speaker();
        Assert.Equal(-4, b.LevelDb(0, 315, 0), 9);                  // halfway between -8 at 270 and 0 at 360
        Assert.Equal(b.LevelDb(0, 90, 0), b.LevelDb(0, 450, 0), 9);
        Assert.Equal(b.LevelDb(0, 270, 0), b.LevelDb(0, -90, 0), 9);
    }

    [Fact]
    public void ElevationsBeyondTheGrid_StayAtTheEndRows()
    {
        var b = new BalloonDirectivity(Azimuths, new double[] { -30, 30 },
            Enumerable.Repeat(new[] { new[] { 0.0, 0, 0, 0 }, new[] { -6.0, -6, -6, -6 } }, 8).ToArray());
        Assert.Equal(0, b.LevelDb(0, 0, -80), 9);
        Assert.Equal(-6, b.LevelDb(0, 0, 80), 9);
        Assert.Equal(-3, b.LevelDb(0, 0, 0), 9);
    }

    [Fact]
    public void Aimed_PutsTheFrontAlongTheAxis_AndTheLeftToItsLeft()
    {
        var aimed = Speaker().Aimed(new Vec3(0, 1, 0));             // facing +y: its left is -x, its right +x
        var d = aimed.ForBand(3);
        Assert.Equal(Math.Pow(10, -12 / 10.0), d.Gain(new Vec3(0, -1, 0)) / d.Gain(new Vec3(0, 1, 0)), 9);
        Assert.Equal(Math.Pow(10, -4 / 10.0), d.Gain(new Vec3(-1, 0, 0)) / d.Gain(new Vec3(0, 1, 0)), 9);
        Assert.Equal(Math.Pow(10, -8 / 10.0), d.Gain(new Vec3(1, 0, 0)) / d.Gain(new Vec3(0, 1, 0)), 9);
        Assert.Equal(Math.Pow(10, -6 / 10.0), d.Gain(new Vec3(0, 0, 1)) / d.Gain(new Vec3(0, 1, 0)), 9);
    }

    [Fact]
    public void ALopsidedBalloon_IsNotTheSameAllRoundTheAxis_WhichAPolarTableCannotBe()
    {
        var d = Speaker().Aimed(new Vec3(1, 0, 0)).ForBand(0);
        var left = d.Gain(new Vec3(0, 1, 0));
        var right = d.Gain(new Vec3(0, -1, 0));
        Assert.True(left / right > 2, $"left {left}, right {right}");
    }

    [Fact]
    public void EveryBandRadiatesTheSamePower_NoMatterHowTheBalloonIsAimed()
    {
        foreach (var axis in new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 1) })
        {
            var aimed = Speaker().Aimed(axis);
            for (var band = 0; band < 8; band++) Assert.Equal(1, MeanGain(aimed.ForBand(band)), 2);
        }
    }

    [Fact]
    public void AimedStraightUp_UsesPlusYAsTheLeft_SoTheGainIsStillFinite()
    {
        var d = Speaker().Aimed(new Vec3(0, 0, 1)).ForBand(0);
        Assert.True(double.IsFinite(d.Gain(new Vec3(1, 0, 0))) && d.Gain(new Vec3(1, 0, 0)) > 0);
        Assert.True(d.Gain(new Vec3(0, 0, 1)) > d.Gain(new Vec3(0, 0, -1)));
    }

    [Fact]
    public void ABalloonIsTiltedWithItsAxis_WithoutRoll()
    {
        // facing 45 degrees up, the source's own "up" tilts back; straight up in the world is then 45 degrees above its front
        var tilt = new Vec3(1, 0, 1).Normalized();
        var d = Speaker().Aimed(tilt).ForBand(0);
        var skyward = new Vec3(0, 0, 1);
        var expected = Math.Pow(10, Speaker().LevelDb(0, 0, 45) / 10);
        Assert.Equal(expected, d.Gain(skyward) / d.Gain(tilt), 9);
    }

    [Fact]
    public void ABadGridIsRefused()
    {
        var levels = Enumerable.Repeat(new[] { new[] { 0.0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 } }, 8).ToArray();
        var two = Enumerable.Repeat(new[] { new[] { 0.0, 0 }, new[] { 0.0, 0 } }, 8).ToArray();
        Assert.Throws<ArgumentException>(() => new BalloonDirectivity(new double[] { 0, 90 }, new double[] { 0, 10 }, two));               // two azimuths
        Assert.Throws<ArgumentException>(() => new BalloonDirectivity(Azimuths, new double[] { 0 }, levels));                               // one elevation
        Assert.Throws<ArgumentException>(() => new BalloonDirectivity(new double[] { 0, 90, 90, 180 }, new double[] { 0, 10 }, levels));  // repeated
        Assert.Throws<ArgumentException>(() => new BalloonDirectivity(new double[] { 0, 90, 180, 360 }, new double[] { 0, 10 }, levels)); // 360 is 0
        Assert.Throws<ArgumentException>(() => new BalloonDirectivity(Azimuths, new double[] { 0, 100 }, levels));                          // beyond the pole
        Assert.Throws<ArgumentException>(() => new BalloonDirectivity(Azimuths, new double[] { 0, 10 }, levels.Take(7).ToArray()));        // seven bands
        Assert.Throws<ArgumentException>(() => new BalloonDirectivity(Azimuths, new double[] { 0, 10 },
            Enumerable.Repeat(new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 0 } }, 8).ToArray()));                                          // a short row
        Assert.Throws<ArgumentOutOfRangeException>(() => Speaker().LevelDb(8, 0, 0));
    }

    // ---- the text format

    /// <summary>A complete grid under the given header, with the cells (after azimuth and elevation) from the function.</summary>
    private static List<string> Table(string header, Func<double, double, string> cells)
    {
        var rows = new List<string> { header };
        foreach (var el in Elevations)
            foreach (var az in Azimuths) rows.Add($"{az},{el},{cells(az, el)}");
        return rows;
    }

    [Fact]
    public void ACsvWithOneColumn_UsesItForEveryBand_InAnyOrderOfRows()
    {
        var text = "# a speaker\r\nazimuth_deg, elevation_deg, level_db\r\n" +
            string.Join("\r\n", Elevations.Reverse().SelectMany(el => Azimuths.Reverse().Select(az => $"{az}, {el}, {-az / 30.0 - Math.Abs(el) / 15.0}")));
        var b = BalloonDirectivity.FromCsv(text);

        Assert.Equal(Azimuths, b.AzimuthsDeg);
        Assert.Equal(Elevations, b.ElevationsDeg);
        for (var band = 0; band < 8; band++) Assert.Equal(-3, b.LevelDb(band, 90, 0), 9);
        Assert.Equal(-9, b.LevelDb(5, 270, 0), 9);
        Assert.Equal(-9 - 6, b.LevelDb(5, 270, 90), 9);
    }

    [Fact]
    public void ACsvWithEightColumns_GivesEachBandItsOwnLevels_InAnyColumnOrder()
    {
        // columns run 8000 Hz down to 63 Hz, and the level at 90 degrees is -1 for the first column, -2 for the next, ... -8 for the last
        var rows = Table("azimuth_deg;elevation_deg;8000 Hz;4000;2000;1000;500;250;125;63",
            (az, el) => string.Join(";", Enumerable.Range(1, 8).Select(i => $"{-(az / 90) * i}")));
        var b = BalloonDirectivity.FromCsv(string.Join("\n", rows).Replace(',', ';'));

        Assert.Equal(-1, b.LevelDb(7, 90, 0), 9);                   // 8 kHz is band 7
        Assert.Equal(-4, b.LevelDb(4, 90, 0), 9);                   // 1 kHz is band 4, the fourth column
        Assert.Equal(-8, b.LevelDb(0, 90, 0), 9);                   // 63 Hz is band 0
    }

    [Theory]
    [InlineData("azimuth_deg,elevation_deg,63,125", "header")]
    [InlineData("azimuth_deg,elevation_deg,63,125,250,500,1000,2000,4000,4000", "twice")]
    [InlineData("azimuth_deg,elevation_deg,63,125,250,500,1000,2000,4000,9000", "twice")]
    public void ABadHeaderIsRefused(string header, string mentions)
    {
        var rows = Table(header, (az, el) => string.Join(",", Enumerable.Repeat("0", header.Split(',').Length - 2)));
        var ex = Assert.Throws<FormatException>(() => BalloonDirectivity.FromCsv(string.Join("\n", rows)));
        Assert.Contains(mentions, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AGridWithAHole_ADuplicate_OrANumberThatIsNotOne_IsRefusedAndTheMessageNamesTheLine()
    {
        var full = Table("azimuth_deg,elevation_deg,level_db", (az, el) => "0");

        var hole = full.Where((_, i) => i != 5).ToList();
        Assert.Contains("not complete", Assert.Throws<FormatException>(() => BalloonDirectivity.FromCsv(string.Join("\n", hole))).Message);

        var twice = full.Append(full[3]).ToList();
        Assert.Contains($"Line {full.Count + 1}", Assert.Throws<FormatException>(() => BalloonDirectivity.FromCsv(string.Join("\n", twice))).Message);

        var bad = full.ToList();
        bad[2] = "90,0,loud";
        Assert.Contains("on line 3", Assert.Throws<FormatException>(() => BalloonDirectivity.FromCsv(string.Join("\n", bad))).Message);

        var ragged = full.ToList();
        ragged[4] = "270,0";
        Assert.Contains("Line 5", Assert.Throws<FormatException>(() => BalloonDirectivity.FromCsv(string.Join("\n", ragged))).Message);

        var round = full.Select(l => l.StartsWith("0,") ? "360," + l[2..] : l);
        Assert.Contains("360", Assert.Throws<FormatException>(() => BalloonDirectivity.FromCsv(string.Join("\n", round))).Message);

        Assert.Throws<FormatException>(() => BalloonDirectivity.FromCsv("azimuth_deg,elevation_deg,level_db\n0,0,0\n"));
    }
}
