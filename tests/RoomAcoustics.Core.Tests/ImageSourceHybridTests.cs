using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class ImageSourceHybridTests
{
    private const int Band1000 = 4;

    private static readonly Vec3 Source = new(2, 2, 1.5);
    private static readonly Vec3 Receiver = new(7, 5, 1.2);

    private static Room3D Box(double alpha, double? scatter = null) =>
        Room3D.Shoebox(10, 7, 3, new SurfaceFinish(
            new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()),
            scatter is { } s ? OctaveSpectrum.Flat(s) : null));

    private static double EnergyWithin(ImpulseResponse ir, int band, double seconds)
    {
        var bins = (int)Math.Round(seconds / ir.BinWidthS);
        return ir.Energy[band].Take(bins).Sum();
    }

    [Fact]
    public void Enumerate_FirstOrderInABox_GivesTheSixMirrorImageDistances()
    {
        // Mirror images of the source in the six walls of a 10 x 7 x 3 m box.
        var (sx, sy, sz) = (Source.X, Source.Y, Source.Z);
        var mirrors = new[]
        {
            new Vec3(-sx, sy, sz), new Vec3(20 - sx, sy, sz),
            new Vec3(sx, -sy, sz), new Vec3(sx, 14 - sy, sz),
            new Vec3(sx, sy, -sz), new Vec3(sx, sy, 6 - sz),
        };
        var expected = mirrors.Select(m => (Receiver - m).Length).OrderBy(d => d).ToArray();

        var firstOrder = ImageSourceEnumerator.Enumerate(Box(0.2), Source, Receiver, 1)
            .Select(a => a.DistanceM).OrderBy(d => d).ToArray();

        Assert.Equal(expected.Length, firstOrder.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], firstOrder[i], 9);
    }

    [Fact]
    public void Enumerate_InABox_FindsEveryLatticeImageOnce()
    {
        // In a box every image lies on a lattice; each lattice point is one geometric path, reflected once per face crossed.
        // The number of lattice points with k crossings is the coefficient of z^k in ((1+z)/(1-z))^3: 6, 18, 38, 66, 102, 146 for k = 1..6.
        var arrivals = ImageSourceEnumerator.Enumerate(Box(0.2), Source, Receiver, 6);

        var counts = arrivals.GroupBy(a => a.Faces.Count).OrderBy(g => g.Key).Select(g => g.Count()).ToArray();

        Assert.Equal(new[] { 6, 18, 38, 66, 102, 146 }, counts);
    }

    [Fact]
    public void Enumerate_EveryArrivalHasTheRequestedOrderOrLess()
    {
        var arrivals = ImageSourceEnumerator.Enumerate(Box(0.2), Source, Receiver, 4);

        Assert.True(arrivals.Count > 6, "Second and higher order paths should exist in a box.");
        Assert.All(arrivals, a => Assert.InRange(a.Faces.Count, 1, 4));
        Assert.All(arrivals, a => Assert.True(a.DistanceM > (Receiver - Source).Length - 1e-9,
            "No reflected path can be shorter than the direct path."));
    }

    [Fact]
    public void Trace_HybridWithFullyDiffuseWalls_IsIdenticalToPureRayTracing()
    {
        // With scattering 1 every reflection is diffuse, so the image paths carry zero weight and the rays count everything.
        var room = Box(0.2, scatter: 1);
        var pure = EnergyRayTracer.Trace(room, Source, Receiver, new RayTraceOptions(RayCount: 50_000, Seed: 3));
        var hybrid = EnergyRayTracer.Trace(room, Source, Receiver, new RayTraceOptions(RayCount: 50_000, Seed: 3, ImageSourceOrder: 6));

        for (var b = 0; b < 8; b++)
            Assert.Equal(pure.Energy[b], hybrid.Energy[b]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void Trace_HybridEarlyEnergyAndClarity_MatchPureRayTracing(double scattering)
    {
        // Both estimate the same expectation: the image sum is exact and the rays are unbiased. The hybrid should just be smoother.
        var room = Box(0.2, scattering);
        var pure = EnergyRayTracer.Trace(room, Source, Receiver, new RayTraceOptions(RayCount: 300_000, Seed: 5));
        var hybrid = EnergyRayTracer.Trace(room, Source, Receiver,
            new RayTraceOptions(RayCount: 300_000, Seed: 5, ImageSourceOrder: 6));

        var pureEarly = EnergyWithin(pure, Band1000, 0.08);
        var hybridEarly = EnergyWithin(hybrid, Band1000, 0.08);
        Assert.InRange(hybridEarly / pureEarly, 0.9, 1.1);

        var c80Pure = RoomAcousticsCalculator.Calculate(pure).C80Db[Band1000];
        var c80Hybrid = RoomAcousticsCalculator.Calculate(hybrid).C80Db[Band1000];
        Assert.InRange(c80Hybrid - c80Pure, -1.0, 1.0);
    }

    [Fact]
    public void Trace_HybridDoesNotChangeTheLateDecay()
    {
        // After the image paths are exhausted, the late energy should follow the ray tracer, not the image sum.
        var room = Box(0.2, 0.5);
        var pure = EnergyRayTracer.Trace(room, Source, Receiver, new RayTraceOptions(RayCount: 300_000, Seed: 5));
        var hybrid = EnergyRayTracer.Trace(room, Source, Receiver,
            new RayTraceOptions(RayCount: 300_000, Seed: 5, ImageSourceOrder: 6));

        var pureT30 = RoomAcousticsCalculator.Calculate(pure).T30S[Band1000];
        var hybridT30 = RoomAcousticsCalculator.Calculate(hybrid).T30S[Band1000];
        Assert.InRange(hybridT30 / pureT30, 0.95, 1.05);
    }
}
