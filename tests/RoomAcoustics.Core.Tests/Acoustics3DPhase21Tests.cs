using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class FanOffsetsTests
{
    private static double[] Degrees(double[] radians) => radians.Select(r => r * 180 / Math.PI).ToArray();

    [Fact]
    public void OneAngle_FansTheLineEvenly_AboutItsMiddle()
    {
        Assert.Equal(new[] { -15.0, -5, 5, 15 }, Degrees(ExtendedSource.FanOffsets(4, new[] { 10 * Math.PI / 180 })), new DoubleComparer());
        Assert.Equal(new[] { -10.0, 0, 10 }, Degrees(ExtendedSource.FanOffsets(3, new[] { 10 * Math.PI / 180 })), new DoubleComparer());
    }

    [Fact]
    public void AGrowingList_PutsTheMiddleStraight_AndSplaysMoreAtTheStartThanTheEnd()
    {
        // Gaps of 8, 4 and 2 degrees between four elements: 0, 8, 12, 14 from the first, less the value midway between the middle two (10).
        var offsets = Degrees(ExtendedSource.FanOffsets(4, new[] { 8 * Math.PI / 180, 4 * Math.PI / 180, 2 * Math.PI / 180 }));

        Assert.Equal(new[] { -10.0, -2, 2, 4 }, offsets, new DoubleComparer());
    }

    [Fact]
    public void AShortList_RepeatsItsLastAngle_AndNoListAimsEveryElementAlike()
    {
        var degree = Math.PI / 180;
        var short3 = Degrees(ExtendedSource.FanOffsets(5, new[] { 6 * degree, 2 * degree }));   // gaps 6, 2, 2, 2: 0, 6, 8, 10, 12; middle 8
        Assert.Equal(new[] { -8.0, -2, 0, 2, 4 }, short3, new DoubleComparer());

        Assert.All(ExtendedSource.FanOffsets(4, Array.Empty<double>()), o => Assert.Equal(0, o));
        Assert.Equal(new[] { 0.0 }, ExtendedSource.FanOffsets(1, new[] { 0.3 }));
    }

    [Fact]
    public void MoreAnglesThanGaps_OrNoElements_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => ExtendedSource.FanOffsets(3, new[] { 0.1, 0.1, 0.1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtendedSource.FanOffsets(0, new[] { 0.1 }));
        Assert.Throws<ArgumentNullException>(() => ExtendedSource.FanOffsets(3, null!));
    }

    private sealed class DoubleComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-9;
        public int GetHashCode(double obj) => 0;
    }
}
