using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class AngleDependentAbsorptionTests
{
    /// <summary>Paris's average, 2 ∫ α(cos θ) cos θ d(cos θ) over 0 to 1, by the midpoint rule.</summary>
    private static double Average(Func<double, double> alphaAt)
    {
        const int n = 20000;
        var sum = 0.0;
        for (var i = 0; i < n; i++) { var u = (i + 0.5) / n; sum += alphaAt(u) * u; }
        return 2 * sum / n;
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.56)]
    [InlineData(4)]
    [InlineData(40)]
    public void TheClosedFormAverage_IsParissIntegralOfTheAngleAbsorption(double xi)
    {
        Assert.Equal(Average(u => AngleDependentAbsorption.AtAngle(xi, u)), AngleDependentAbsorption.RandomIncidence(xi), 6);
    }

    [Fact]
    public void TheAverageIsLargest_0951_NearXi156()
    {
        Assert.InRange(AngleDependentAbsorption.Peak.Xi, 1.55, 1.58);
        Assert.Equal(0.951, AngleDependentAbsorption.Peak.Alpha, 3);
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.1)]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(0.9)]
    [InlineData(0.98)]   // above the largest average: scaled up
    [InlineData(1.0)]
    public void ASurfaceInADiffuseField_AbsorbsWhatItsCoefficientSays(double alpha)
    {
        var xi = AngleDependentAbsorption.ImpedanceFor(alpha);
        Assert.True(xi >= AngleDependentAbsorption.Peak.Xi - 1e-9, "the high-impedance side");
        Assert.Equal(alpha, Average(u => AngleDependentAbsorption.Alpha(alpha, xi, u)), 4);
    }

    [Fact]
    public void SoundHeadOnIsAbsorbedLessThanTheAverage_ObliqueSoundMore_UpToAPeakNearGrazing_AndGrazingSoundHardlyAtAll()
    {
        var xi = AngleDependentAbsorption.ImpedanceFor(0.3);
        double At(double cos) => AngleDependentAbsorption.Alpha(0.3, xi, cos);
        Assert.True(At(1) < 0.3);
        Assert.Equal(1, At(1 / xi), 9);                       // the peak, where ξ cos θ = 1: all of it absorbed
        Assert.True(At(1 / xi) > At(0.5) && At(0.5) > At(1));
        Assert.True(At(1e-4) < 0.01);
        Assert.Equal(0, AngleDependentAbsorption.Alpha(0, AngleDependentAbsorption.ImpedanceFor(0), 0.5));
    }
}

public class AngleDependentTraceTests
{
    private static Room3D Room(double alpha, double scatter, double l = 30, double w = 10, double h = 10) =>
        Room3D.Shoebox(l, w, h, new SurfaceFinish(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scatter)));

    [Fact]
    public void AFloorReflectionMetAt73DegreesFromTheNormal_LosesWhatThatAngleTakes_MoreThanTheCoefficient()
    {
        // source and receiver 4 m up and 26 m apart: the floor's reflection arrives 1.2 m after the direct sound, alone in its bin, at cos 0.29
        var room = Room(0.5, 0);
        var options = new RayTraceOptions(RayCount: 2000, ImageSourceOrder: 1);
        double FloorBin(bool angles) => EnergyRayTracer.Trace(room, new Vec3(2, 5, 4), new Vec3(28, 5, 4), options with { AngleDependentAbsorption = angles }).Energy[4][79];
        var plain = FloorBin(false);
        var angled = FloorBin(true);
        var xi = AngleDependentAbsorption.ImpedanceFor(0.5);
        var cos = 8 / Math.Sqrt(26 * 26 + 8 * 8);
        Assert.True(plain > 0);
        Assert.Equal((1 - AngleDependentAbsorption.Alpha(0.5, xi, cos)) / 0.5, angled / plain, 6);
        Assert.True(angled < plain);    // at cos 0.29, ξ cos θ is about 2.8: near the peak, so it takes about 0.78 rather than 0.5
    }

    [Fact]
    public void InADiffuseRoom_TheDecayIsMuchAsBefore_SinceEverySurfaceKeepsItsAverage()
    {
        var room = Room(0.2, 0.6, 20, 14, 8);
        double T30(bool angles) => RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(room, new Vec3(4, 7, 1.5), new Vec3(14, 5, 1.2),
            new RayTraceOptions(RayCount: 20_000, ImageSourceOrder: 2, AngleDependentAbsorption: angles))).T30S[4];
        var plain = T30(false);
        Assert.InRange(T30(true), 0.85 * plain, 1.2 * plain);
    }
}
