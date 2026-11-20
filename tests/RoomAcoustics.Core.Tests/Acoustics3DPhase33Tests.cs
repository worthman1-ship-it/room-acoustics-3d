using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class CoverageDirectivityTests
{
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
    public void TheLevelIsSixDecibelsDownAtHalfTheCoverageAngle_InEachPlane()
    {
        var b = BalloonDirectivity.FromCoverage(90, 40);

        Assert.Equal(0, b.LevelDb(3, 0, 0), 9);
        Assert.Equal(-6, b.LevelDb(3, 45, 0), 9);                   // half of 90 degrees to the left
        Assert.Equal(-6, b.LevelDb(3, 315, 0), 9);                  // and to the right
        Assert.Equal(-6, b.LevelDb(3, 0, 20), 9);                   // half of 40 degrees up
        Assert.Equal(-6, b.LevelDb(3, 0, -20), 9);                  // and down
    }

    [Fact]
    public void TheLevelFallsWithTheSquareOfTheAngle()
    {
        var b = BalloonDirectivity.FromCoverage(90, 40);

        Assert.Equal(-1.5, b.LevelDb(3, 22.5, 0), 9);               // half the angle, a quarter of the fall
        // the two planes add: 45 degrees across (shortened by the cosine of the 10 degrees up) and 10 degrees up
        var cos = Math.Cos(10 * Math.PI / 180);
        Assert.Equal(-6 * (cos * cos + 0.25), b.LevelDb(3, 45, 10), 9);
    }

    [Fact]
    public void BeyondTheBeam_TheLevelStaysAtTheFloor_BehindTheSourceToo()
    {
        var b = BalloonDirectivity.FromCoverage(60, 30, -25);

        Assert.Equal(-25, b.LevelDb(3, 180, 0), 9);
        Assert.Equal(-25, b.LevelDb(3, 90, 40), 9);
        Assert.Equal(-25, b.LevelDb(3, 0, 90), 9);
        Assert.Equal(-25, BalloonDirectivity.FromCoverage(60, 30).LevelDb(3, 180, 0) - 5, 9);   // the default floor is -20 dB
    }

    [Fact]
    public void TheSameBeamIsUsedInEveryBand_AndIsTheSameOnBothSides()
    {
        var b = BalloonDirectivity.FromCoverage(120, 60);

        for (var band = 1; band < 8; band++)
            Assert.Equal(b.LevelDb(0, 33, 12), b.LevelDb(band, 33, 12), 12);
        Assert.Equal(b.LevelDb(2, 33, 12), b.LevelDb(2, 360 - 33, 12), 9);
        Assert.Equal(b.LevelDb(2, 33, 12), b.LevelDb(2, 33, -12), 9);
    }

    [Fact]
    public void AtThePoles_TheLevelDoesNotDependOnTheAzimuth()
    {
        var b = BalloonDirectivity.FromCoverage(40, 180);           // the vertical coverage reaches the pole: 6 dB down there

        foreach (var az in new[] { 0.0, 37.5, 90, 180, 262.5 })
        {
            Assert.Equal(-6, b.LevelDb(4, az, 90), 9);
            Assert.Equal(-6, b.LevelDb(4, az, -90), 9);
        }
    }

    [Fact]
    public void AimedAlongAnAxis_TheFrontIsLouderThanTheBackByTheFloor_AndEveryBandRadiatesTheSamePower()
    {
        var aimed = BalloonDirectivity.FromCoverage(90, 40, -20).Aimed(new Vec3(0, 1, 0));
        var d = aimed.ForBand(5);

        Assert.Equal(100, d.Gain(new Vec3(0, 1, 0)) / d.Gain(new Vec3(0, -1, 0)), 6);
        for (var band = 0; band < 8; band++) Assert.Equal(1, MeanGain(aimed.ForBand(band)), 2);
    }

    [Fact]
    public void ANarrowerBeamPutsMoreOfThePowerOnTheAxis()
    {
        var onAxis = new Vec3(1, 0, 0);
        var narrow = BalloonDirectivity.FromCoverage(40, 20).Aimed(onAxis).ForBand(3).Gain(onAxis);
        var wide = BalloonDirectivity.FromCoverage(120, 60).Aimed(onAxis).ForBand(3).Gain(onAxis);

        Assert.True(narrow > wide * 1.5, $"narrow {narrow}, wide {wide}");
        Assert.True(wide > 1);
    }

    [Fact]
    public void AFloorOfZero_IsOmnidirectional()
    {
        var d = BalloonDirectivity.FromCoverage(60, 30, 0).Aimed(new Vec3(1, 0, 0)).ForBand(2);

        Assert.Equal(1, d.Gain(new Vec3(1, 0, 0)), 9);
        Assert.Equal(1, d.Gain(new Vec3(-0.3, 0.9, 0.2).Normalized()), 9);
    }

    [Theory]
    [InlineData(0, 40, -20)]
    [InlineData(361, 40, -20)]
    [InlineData(double.NaN, 40, -20)]
    [InlineData(90, 0, -20)]
    [InlineData(90, 181, -20)]
    [InlineData(90, 40, -61)]
    [InlineData(90, 40, 1)]
    [InlineData(90, 40, double.NaN)]
    public void ImpossibleCoverageOrFloor_IsRefused(double horizontal, double vertical, double floor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BalloonDirectivity.FromCoverage(horizontal, vertical, floor));
    }
}
