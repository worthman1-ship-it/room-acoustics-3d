using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class DecayAnalysisTests
{
    private const double Dt = 0.001;

    /// <summary>Energy that falls 60 dB in <paramref name="t60"/> seconds.</summary>
    private static double[] Exponential(double t60, int bins = 2000) =>
        Enumerable.Range(0, bins).Select(i => Math.Pow(10, -6.0 * i * Dt / t60)).ToArray();

    [Fact]
    public void SchroederCurve_StartsAtZero_AndNeverRises()
    {
        var curve = DecayAnalysis.SchroederDb(Exponential(0.8));

        Assert.Equal(0, curve[0], 9);
        for (var i = 1; i < curve.Length; i++) Assert.True(curve[i] <= curve[i - 1]);
    }

    [Fact]
    public void SchroederCurve_OfAnExponential_IsAStraightLineAtTheSameRate()
    {
        var fit = DecayAnalysis.T30Fit(DecayAnalysis.SchroederDb(Exponential(0.8)), Dt);

        Assert.NotNull(fit);
        Assert.Equal(0.8, fit!.Value.ReverberationTimeS, 2);
        Assert.Equal(0, fit.Value.InterceptDb, 1);
        Assert.Equal(-60 / 0.8, fit.Value.SlopeDbPerS, 1);
        Assert.Equal(-30, fit.Value.LevelAt(0.4), 1);
    }

    [Fact]
    public void ABinWithNothingAfterIt_IsNegativeInfinity()
    {
        var h = new double[10];
        h[2] = 1;

        var curve = DecayAnalysis.SchroederDb(h);

        Assert.Equal(0, curve[2], 9);
        Assert.True(double.IsNegativeInfinity(curve[3]));
    }

    [Fact]
    public void Fit_IsNull_WhenTheCurveDoesNotFallThroughTheRange()
    {
        Assert.Null(DecayAnalysis.T30Fit(new double[] { 0, 0, 0, 0 }, Dt));
        Assert.Null(DecayAnalysis.T30Fit(DecayAnalysis.SchroederDb(new double[100]), Dt));
    }

    [Fact]
    public void Calculator_TakesItsDecayTimesFromTheSameFits()
    {
        var h = Exponential(0.8);
        var ir = new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));
        var curve = DecayAnalysis.SchroederDb(h);

        var m = RoomAcousticsCalculator.Calculate(ir);

        Assert.Equal(DecayAnalysis.T30Fit(curve, Dt)!.Value.ReverberationTimeS, m.T30S[3], 12);
        Assert.Equal(DecayAnalysis.T20Fit(curve, Dt)!.Value.ReverberationTimeS, m.T20S[3], 12);
        Assert.Equal(DecayAnalysis.EarlyDecayFit(curve, Dt)!.Value.ReverberationTimeS, m.EarlyDecayTimeS[3], 12);
    }

    [Fact]
    public void EnergyDb_IsRelativeToTheStrongestBin_AndDropsWhatIsBelowTheFloor()
    {
        var level = DecayAnalysis.EnergyDb(new[] { 0.0, 1.0, 0.1, 1e-9 }, -60);

        Assert.True(double.IsNegativeInfinity(level[0]));
        Assert.Equal(0, level[1], 9);
        Assert.Equal(-10, level[2], 9);
        Assert.True(double.IsNegativeInfinity(level[3])); // -90 dB, below the floor
    }

    [Fact]
    public void EnergyDb_OfNothing_IsAllNegativeInfinity()
    {
        Assert.All(DecayAnalysis.EnergyDb(new double[5], -60), v => Assert.True(double.IsNegativeInfinity(v)));
        Assert.Empty(DecayAnalysis.EnergyDb(Array.Empty<double>(), -60));
    }
}

public class ReflectionPathAnalysisTests
{
    private static SurfaceFinish Finish(double alpha, double scattering = 0) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scattering));

    private static readonly Vec3 Source = new(2, 2, 1.5);
    private static readonly Vec3 Receiver = new(6, 4, 1.2);

    private static Room3D Box(double alpha = 0.2, double scattering = 0) => Room3D.Shoebox(10, 7, 3, Finish(alpha, scattering));

}

public class EnumeratorAndMapKeepTests
{
    private static readonly Vec3 Source = new(2, 2, 1.5);
    private static readonly Vec3 Receiver = new(6, 4, 1.2);

    private static Room3D Box() =>
        Room3D.Shoebox(10, 7, 3, new SurfaceFinish(new AbsorptionMaterial("t", "User", Enumerable.Repeat((double?)0.2, 8).ToArray()), OctaveSpectrum.Flat(0.3)));

    [Fact]
    public void TheEnumerator_KeepsNoRoutesUnlessAsked()
    {
        var plain = ImageSourceEnumerator.Enumerate(Box(), Source, Receiver, 2);
        var withRoutes = ImageSourceEnumerator.Enumerate(Box(), Source, Receiver, 2, withPaths: true);

        Assert.All(plain, a => Assert.Null(a.Path));
        Assert.Equal(plain.Count, withRoutes.Count);
        Assert.All(withRoutes, a => Assert.Equal(a.Faces.Count + 2, a.Path!.Count));
    }

    [Fact]
    public void TheMap_KeepsTheImpulseResponses_WithoutTheDirectionArrays()
    {
        var room = Box();
        var receivers = new[] { new Vec3(4, 3, 1.2), new Vec3(7, 5, 1.2) };
        var options = new RayTraceOptions(RayCount: 3000, ImageSourceOrder: 2, MaxTimeS: 0.5);

        var map = AcousticMap.Calculate(room, Source, receivers, options);
        var direct = EnergyRayTracer.TraceReceivers(room, Source, receivers, options);

        Assert.NotNull(map.Responses);
        Assert.Equal(2, map.Responses!.Count);
        for (var r = 0; r < 2; r++)
        {
            Assert.Null(map.Responses[r].LateralEnergy);
            Assert.Null(map.Responses[r].LateralAmplitudeEnergy);
            for (var b = 0; b < 8; b++) Assert.Equal(direct[r].Energy[b], map.Responses[r].Energy[b]);
        }
    }
}
