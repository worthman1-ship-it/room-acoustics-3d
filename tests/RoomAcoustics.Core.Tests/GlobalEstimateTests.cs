using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>Millington-Sette, Arau-Puchades and Fitzroy: the other classic global reverberation-time estimates.</summary>
public sealed class GlobalEstimateTests
{
    private static Surface Face(string name, double area, double alpha, int axis = -1) =>
        new(name, area, new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()), axis);

    private static double T(ReverberationMethod method, double volume, IReadOnlyList<Surface> surfaces) =>
        ReverberationCalculator.Calculate(volume, surfaces, new ReverberationOptions(method)).T60[0];

    // a 10 x 8 x 4 m room: floor and ceiling (z) 80 m2 each, the long walls (y) 40 m2 each, the end walls (x) 32 m2 each
    private static List<Surface> Box(double floor, double ceiling, double longWalls, double endWalls) => new()
    {
        Face("floor", 80, floor, 2), Face("ceiling", 80, ceiling, 2),
        Face("long a", 40, longWalls, 1), Face("long b", 40, longWalls, 1), Face("end a", 32, endWalls, 0), Face("end b", 32, endWalls, 0),
    };

    private const double Volume = 320;

    [Fact]
    public void WithOneAbsorptionEverywhere_AllThreeAgreeWithEyring()
    {
        var room = Box(0.2, 0.2, 0.2, 0.2);
        var eyring = T(ReverberationMethod.EyringNorris, Volume, room);
        Assert.Equal(eyring, T(ReverberationMethod.Millington, Volume, room), 9);
        Assert.Equal(eyring, T(ReverberationMethod.ArauPuchades, Volume, room), 9);
        Assert.Equal(eyring, T(ReverberationMethod.Fitzroy, Volume, room), 9);
    }

    [Fact]
    public void WhenEverySurfaceFullyAbsorbs_EyringIsFiniteAndAgreesWithTheOthers()
    {
        // an absorption of 1 is taken as 0.999 in every logarithm, so Eyring does not collapse to zero while Millington does not
        var room = Box(1, 1, 1, 1);
        var eyring = T(ReverberationMethod.EyringNorris, Volume, room);
        Assert.True(eyring > 0 && double.IsFinite(eyring));
        Assert.Equal(eyring, T(ReverberationMethod.Millington, Volume, room), 9);
        Assert.Equal(eyring, T(ReverberationMethod.Fitzroy, Volume, room), 9);
    }

    [Fact]
    public void Millington_IsTheSumOfEachSurfacesOwnEyringAbsorption()
    {
        var room = Box(0.05, 0.6, 0.1, 0.3);
        var c = 331.3 + 0.606 * 20;
        var absorption = room.Sum(s => -s.AreaSqm * Math.Log(1 - s.Material.Alpha[0]!.Value));
        Assert.Equal(55.3 * Volume / (c * absorption), T(ReverberationMethod.Millington, Volume, room), 9);
        // unevenly absorbing surfaces absorb more than their mean says, so the time is shorter than Eyring's
        Assert.True(T(ReverberationMethod.Millington, Volume, room) < T(ReverberationMethod.EyringNorris, Volume, room));
    }

    [Fact]
    public void ArauPuchades_AndFitzroy_AreTheGeometricAndArithmeticMeansOfThreeAxisTimes()
    {
        var room = Box(0.05, 0.6, 0.1, 0.3);
        var c = 331.3 + 0.606 * 20;
        double Axis(double alphaMean)
        {
            const double total = 304;
            return 55.3 * Volume / (c * -total * Math.Log(1 - alphaMean));
        }
        var tz = Axis((0.05 * 80 + 0.6 * 80) / 160);
        var ty = Axis(0.1);
        var tx = Axis(0.3);
        var arau = Math.Pow(tz, 160 / 304.0) * Math.Pow(ty, 80 / 304.0) * Math.Pow(tx, 64 / 304.0);
        var fitzroy = tz * 160 / 304.0 + ty * 80 / 304.0 + tx * 64 / 304.0;
        Assert.Equal(arau, T(ReverberationMethod.ArauPuchades, Volume, room), 9);
        Assert.Equal(fitzroy, T(ReverberationMethod.Fitzroy, Volume, room), 9);
        Assert.True(fitzroy > arau, "an arithmetic mean is above the geometric mean unless the times are equal");
    }

    [Fact]
    public void TheDirectionalOnes_NeedEverySurfaceAssignedToAnAxis()
    {
        var room = new List<Surface> { Face("a", 50, 0.2), Face("b", 50, 0.2, 0) };
        Assert.Throws<ArgumentException>(() => T(ReverberationMethod.ArauPuchades, 100, room));
        Assert.Throws<ArgumentException>(() => T(ReverberationMethod.Fitzroy, 100, room));
        Assert.True(T(ReverberationMethod.Millington, 100, room) > 0); // Millington needs no axes
    }

    [Fact]
    public void AnAlphaOfOne_DoesNotGiveAnInfiniteAbsorption()
    {
        var room = Box(1.0, 0.2, 0.2, 0.2);
        Assert.True(double.IsFinite(T(ReverberationMethod.Millington, Volume, room)));
    }
}
