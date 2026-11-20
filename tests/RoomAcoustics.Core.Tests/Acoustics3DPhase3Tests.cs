using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class Acoustics3DPhase3Tests
{
    private const int Band500 = 3;
    private const int Band1000 = 4;

    private static readonly Vec3 Source = new(2, 2, 1.5);
    private static readonly Vec3 Receiver = new(7, 5, 1.2);

    private static SurfaceFinish Finish(double alpha, double? scatter = null) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()),
            scatter is { } s ? OctaveSpectrum.Flat(s) : null);

    private static double EnergyWithin(ImpulseResponse ir, int band, double seconds)
    {
        var bins = (int)Math.Round(seconds / ir.BinWidthS);
        return ir.Energy[band].Take(bins).Sum();
    }

    [Fact]
    public void Directivity_OmnidirectionalIsUnity_AndCardioidIsNormalisedToUnitMean()
    {
        // 1 + cos θ averages to 1 over the sphere, so forward gain is 2 and backward is 0, with no further scaling.
        var cardioid = SourceDirectivity.Cardioid(new Vec3(1, 0, 0));

        Assert.Equal(1, SourceDirectivity.Omnidirectional.Gain(new Vec3(0, 0, 1)), 12);
        // The normalisation uses a 20,000-point lattice, so it is accurate to about 1e-6, not exact.
        Assert.Equal(2, cardioid.Gain(new Vec3(1, 0, 0)), 5);
        Assert.Equal(0, cardioid.Gain(new Vec3(-1, 0, 0)), 5);
    }

    [Fact]
    public void Directivity_RejectsAPatternThatIsZeroEverywhere()
    {
        Assert.Throws<ArgumentException>(() => new SourceDirectivity(_ => 0));
    }

    [Fact]
    public void Trace_CardioidFacingTheReceiver_DeliversMoreEarlyEnergyThanFacingAway()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, scatter: 0.5));
        var towards = SourceDirectivity.Cardioid(Receiver - Source);
        var away = SourceDirectivity.Cardioid(Source - Receiver);

        var facing = EnergyRayTracer.Trace(room, Source, Receiver, new RayTraceOptions(RayCount: 200_000, Seed: 4, Directivity: towards));
        var facingAway = EnergyRayTracer.Trace(room, Source, Receiver, new RayTraceOptions(RayCount: 200_000, Seed: 4, Directivity: away));

        var ratio = EnergyWithin(facing, Band1000, 0.08) / EnergyWithin(facingAway, Band1000, 0.08);
        Assert.True(ratio > 1.5, $"Early energy facing the receiver should clearly exceed facing away; ratio was {ratio:0.00}.");
    }

    [Fact]
    public void TraceReceivers_EachReceiverMatchesItsOwnSingleTrace()
    {
        // Each ray's hits do not depend on the other receivers, so with the same seed the bins must agree exactly.
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, scatter: 0.5));
        var second = new Vec3(3, 5, 1.2);
        var options = new RayTraceOptions(RayCount: 30_000, Seed: 9, ImageSourceOrder: 3);

        var together = EnergyRayTracer.TraceReceivers(room, Source, new[] { Receiver, second }, options);
        var alone = EnergyRayTracer.Trace(room, Source, Receiver, options);
        var secondAlone = EnergyRayTracer.Trace(room, Source, second, options);

        for (var b = 0; b < 8; b++)
        {
            Assert.Equal(alone.Energy[b], together[0].Energy[b]);
            Assert.Equal(secondAlone.Energy[b], together[1].Energy[b]);
        }
    }

    [Fact]
    public void DistanceToBoundary_AtTheCentreOfAShoebox_IsHalfTheShortestDimension()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.1));

        Assert.Equal(1.5, room.DistanceToBoundary(new Vec3(5, 3.5, 1.5)), 9);
        Assert.True(room.DistanceToBoundary(new Vec3(-1, 3.5, 1.5)) < 0);
    }

    [Fact]
    public void ReceiverGrid_KeepsOnlyPointsClearOfEverySurface()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.1));

        // Centres at 0.5, 1.5, ... 9.5 m in x and 0.5 ... 6.5 m in y. With 1 m clearance at 1.5 m height:
        // x in [1, 9] keeps 8 centres, y in [1, 6] keeps 5.
        var points = ReceiverGrid.Horizontal(room, 1.5, 1.0, 1.0);

        Assert.Equal(40, points.Count);
        Assert.All(points, p => Assert.True(room.DistanceToBoundary(p) >= 1.0 - 1e-9));
    }

    [Fact]
    public void AcousticMap_InADiffuseBox_T30IsUniformAcrossTheReceiverGrid()
    {
        // A diffusely reflecting box should decay the same everywhere. The map must show that, and sit near Eyring.
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, scatter: 1));
        var grid = ReceiverGrid.Horizontal(room, 1.2, 2.0, 0.5);

        var map = AcousticMap.Calculate(room, Source, grid, new RayTraceOptions(RayCount: 100_000, Seed: 2));

        var t30 = map.Values(m => m.T30S, 500);
        Assert.Equal(grid.Count, t30.Length);
        Assert.True(t30.Max() / t30.Min() < 1.1,
            $"T30 should be uniform in a diffuse box; ranged from {t30.Min():0.000} s to {t30.Max():0.000} s.");

        var surfaces = room.Faces.Select(f => new Surface(f.Name, f.AreaM2, f.Finish.Material)).ToList();
        var eyring = ReverberationCalculator.Calculate(room.VolumeM3, surfaces,
            new ReverberationOptions(ReverberationMethod.EyringNorris)).T60[Band500];
        var mean = t30.Average();
        Assert.InRange(mean / eyring, 0.98, 1.1);
    }
}
