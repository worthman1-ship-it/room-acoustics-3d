using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class RoomSectionTests
{
    private static SurfaceFinish Finish(double alpha = 0.2) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    private static double Length(IEnumerable<SectionSegment> segments) =>
        segments.Sum(s => Math.Sqrt((s.U2 - s.U1) * (s.U2 - s.U1) + (s.V2 - s.V1) * (s.V2 - s.V1)));

    [Fact]
    public void AGridOnAPlaneOfConstantY_HasThatY_AndIsClearOfTheSurfaces()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        var points = RoomSection.Grid(room, SectionAxis.Y, 2.5, 1.0, 0.5);

        Assert.All(points, p => Assert.Equal(2.5, p.Y, 9));
        Assert.All(points, p => Assert.True(room.DistanceToBoundary(p) >= 0.5));
        // x = 0.5 .. 9.5 and z = 0.5 .. 2.5, in 1 m cells: 10 across, 3 up.
        Assert.Equal(30, points.Count);
        Assert.Equal(new[] { 0.5, 1.5, 2.5 }, points.Select(p => p.Z).Distinct().OrderBy(z => z));
    }

    [Fact]
    public void AGridOnAPlaneOfConstantX_RunsAlongY()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        var points = RoomSection.Grid(room, SectionAxis.X, 4, 1.0, 0.5);

        Assert.All(points, p => Assert.Equal(4, p.X, 9));
        Assert.Equal(7 * 3, points.Count);
        Assert.Equal(7, points.Select(p => p.Y).Distinct().Count());
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(7.2)]
    [InlineData(0.2)]
    public void APlaneOutsideTheRoomOrWithinTheClearanceOfAWall_HasNoGrid(double y)
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        var ex = Assert.Throws<ArgumentException>(() => RoomSection.Grid(room, SectionAxis.Y, y, 1.0, 0.5));
        Assert.Contains("No grid point", ex.Message);
    }

    [Fact]
    public void ABadSpacingOrClearance_IsRefused()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        Assert.Throws<ArgumentOutOfRangeException>(() => RoomSection.Grid(room, SectionAxis.Y, 2, 0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomSection.Grid(room, SectionAxis.Y, 2, 1, -1));
    }

    [Fact]
    public void TheOutlineOfAShoebox_IsTheRectangleOfTheCut()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        var outline = RoomSection.Outline(room, SectionAxis.Y, 2.5);

        Assert.Equal(4, outline.Count);
        Assert.Equal(2 * (10 + 3), Length(outline), 9);
        Assert.All(outline, s =>
        {
            Assert.InRange(s.U1, -1e-9, 10 + 1e-9);
            Assert.InRange(s.U2, -1e-9, 10 + 1e-9);
            Assert.InRange(s.V1, -1e-9, 3 + 1e-9);
        });
    }

    [Fact]
    public void TheOutlineAcrossX_HasYAcrossTheSection()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        var outline = RoomSection.Outline(room, SectionAxis.X, 4);

        Assert.Equal(2 * (7 + 3), Length(outline), 9);
        Assert.Equal(7, outline.SelectMany(s => new[] { s.U1, s.U2 }).Max(), 9);
    }

    [Fact]
    public void ThePlaneOfAWall_GivesNoOutlineForThatWall_AndAPlaneOutsideGivesNothing()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        Assert.Empty(RoomSection.Outline(room, SectionAxis.Y, 20));
        // On the wall y = 0 the other four faces still meet the plane along their edges, but the wall itself is skipped.
        Assert.DoesNotContain(RoomSection.Outline(room, SectionAxis.Y, 0), s => s.U1 == s.U2 && s.V1 == s.V2);
    }

    [Fact]
    public void AnLRoomCutThroughOneArm_IsThatArmsRectangle()
    {
        var room = Phase5Fixtures.LRoom(Finish(), 3);

        var alongTheXArm = RoomSection.Outline(room, SectionAxis.Y, 0.5);   // x from 0 to 4
        var acrossTheYArm = RoomSection.Outline(room, SectionAxis.Y, 2.5);  // x from 0 to 1

        Assert.Equal(2 * (4 + 3), Length(alongTheXArm), 9);
        Assert.Equal(2 * (1 + 3), Length(acrossTheYArm), 9);
    }
}

public class MapStatisticsTests
{
    [Fact]
    public void TheStatistics_OfAKnownSet_AreThoseOfThatSet()
    {
        var stats = MapStatistics.Of(new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 })!;

        Assert.Equal(8, stats.Count);
        Assert.Equal(5, stats.Mean, 12);
        Assert.Equal(2, stats.StdDev, 12);      // the classic example: population standard deviation 2
        Assert.Equal(4.5, stats.Median, 12);
        Assert.Equal(2, stats.Min);
        Assert.Equal(9, stats.Max);
        Assert.Equal(7, stats.Spread, 12);
    }

    [Fact]
    public void AnOddCount_HasTheMiddleValueForItsMedian()
    {
        Assert.Equal(3, MapStatistics.Of(new[] { 9.0, 1, 3 })!.Median);
    }

    [Fact]
    public void NonFiniteValues_AreLeftOut_AndAnEmptySetHasNoStatistics()
    {
        var stats = MapStatistics.Of(new[] { 1.0, double.NaN, 3, double.PositiveInfinity })!;

        Assert.Equal(2, stats.Count);
        Assert.Equal(2, stats.Mean, 12);
        Assert.Null(MapStatistics.Of(new[] { double.NaN }));
        Assert.Null(MapStatistics.Of(Array.Empty<double>()));
    }

    [Fact]
    public void TheFractionWithinATolerance_CountsEitherSideOfTheCentre()
    {
        var values = new[] { 1.0, 2, 3, 4, 5, double.NaN };

        Assert.Equal(0.6, MapStatistics.FractionWithin(values, 3, 1), 12);   // 2, 3, 4
        Assert.Equal(0.2, MapStatistics.FractionWithin(values, 3, 0), 12);   // only the 3
        Assert.Equal(0, MapStatistics.FractionWithin(Array.Empty<double>(), 3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapStatistics.FractionWithin(values, 3, -1));
    }
}
