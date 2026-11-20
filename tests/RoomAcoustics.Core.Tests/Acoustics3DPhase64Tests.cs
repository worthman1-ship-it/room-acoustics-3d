using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class RoomVolumeTests
{
    private static Room3D Box() => Room3D.Shoebox(6, 4, 3, new SurfaceFinish(new AbsorptionMaterial("flat", "User", Enumerable.Repeat((double?)0.2, 8).ToArray()), OctaveSpectrum.Flat(0.1)));

    [Fact]
    public void ParseHeights_AcceptsCommasSpacesAndLines_SortsAndDropsRepeats()
    {
        Assert.Equal(new[] { 0.6, 1.2, 1.8 }, RoomVolume.ParseHeights("1.8, 0.6;1.2\r\n1.2"));
        Assert.Equal(new[] { 1.5 }, RoomVolume.ParseHeights("1.5"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ,; ")]
    [InlineData("-1")]
    [InlineData("1 2 3 4 5 6 7 8 9 10 11 12 13")]
    public void ParseHeights_RefusesNothingNegativesAndTooMany(string text) =>
        Assert.Throws<ArgumentException>(() => RoomVolume.ParseHeights(text));

    [Theory]
    [InlineData("abc")]
    [InlineData("1,x")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ParseHeights_RefusesText_ThatIsNotAFiniteNumber(string text) =>
        Assert.Throws<FormatException>(() => RoomVolume.ParseHeights(text));

    [Fact]
    public void Grid_RepeatsThePlanGridAtEachHeight_LowestFirst()
    {
        var room = Box();
        var plan = ReceiverGrid.Horizontal(room, 1.0, 1.0, 0.5);
        var grid = RoomVolume.Grid(room, new[] { 2.0, 1.0 }, 1.0, 0.5, out var empty);

        Assert.Empty(empty);
        Assert.Equal(plan.Count * 2, grid.Count);
        Assert.All(grid.Take(plan.Count), r => Assert.Equal(1.0, r.Z));
        Assert.All(grid.Skip(plan.Count), r => Assert.Equal(2.0, r.Z));
        Assert.Equal(plan.Select(p => (p.X, p.Y)), grid.Take(plan.Count).Select(p => (p.X, p.Y)));
    }

    [Fact]
    public void AHeightWithNoClearPoint_IsReported_AndLeftOut_ButNoneAtAllIsAnError()
    {
        var room = Box();   // 3 m high: 0.5 m clearance rules out 2.9 m
        var grid = RoomVolume.Grid(room, new[] { 1.5, 2.9 }, 1.0, 0.5, out var empty);
        Assert.Equal(new[] { 2.9 }, empty);
        Assert.All(grid, r => Assert.Equal(1.5, r.Z));

        Assert.Throws<ArgumentException>(() => RoomVolume.Grid(room, new[] { 2.9, 5.0 }, 1.0, 0.5, out _));
        Assert.Throws<ArgumentException>(() => RoomVolume.Grid(room, Array.Empty<double>(), 1.0, 0.5, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomVolume.Grid(room, new[] { 1.5 }, 0, 0.5, out _));
    }
}
