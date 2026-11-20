using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The receiver grid makes a map faster without changing a single number: every receiver gets exactly the answer it would get alone.</summary>
public class ReceiverIndexTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0.2));

    [Fact]
    public void ManyReceivers_GetTheSameResponses_AsEachOneTracedAlone()
    {
        var room = Room3D.Shoebox(12, 8, 4, Finish(0.15));
        var source = new Vec3(2, 4, 1.6);
        var receivers = new List<Vec3>();
        for (var x = 1.5; x < 11.5; x += 1.0)
            for (var y = 1.5; y < 7.5; y += 1.5)
                receivers.Add(new Vec3(x, y, 1.2));
        Assert.True(receivers.Count >= ReceiverIndexThreshold);

        var options = new RayTraceOptions(RayCount: 4000, Seed: 7);
        var together = EnergyRayTracer.TraceReceivers(room, source, receivers, options);
        foreach (var i in new[] { 0, 7, receivers.Count / 2, receivers.Count - 1 })
        {
            var alone = EnergyRayTracer.TraceReceivers(room, source, new[] { receivers[i] }, options)[0];
            for (var band = 0; band < 8; band++)
                Assert.Equal(alone.Energy[band], together[i].Energy[band]);
        }
    }

    [Fact]
    public void ASegmentIsListedWithEveryReceiverSphereItTouches_AndNoReceiverTwice()
    {
        var rng = new Random(3);
        var receivers = Enumerable.Range(0, 200).Select(_ => new Vec3(1 + rng.NextDouble() * 10, 1 + rng.NextDouble() * 6, 1 + rng.NextDouble() * 2)).ToList();
        const double radius = 0.5;
        var index = new ReceiverIndex(receivers, radius);
        var scratch = index.NewScratch();
        for (var n = 0; n < 2000; n++)
        {
            // segments from anywhere (also outside the grid) in any direction
            var start = new Vec3(rng.NextDouble() * 14 - 1, rng.NextDouble() * 10 - 1, rng.NextDouble() * 5 - 0.5);
            var z = 2 * rng.NextDouble() - 1;
            var phi = 2 * Math.PI * rng.NextDouble();
            var direction = new Vec3(Math.Sqrt(1 - z * z) * Math.Cos(phi), Math.Sqrt(1 - z * z) * Math.Sin(phi), z);
            var length = rng.NextDouble() * 15;
            index.Query(start, direction, length, scratch);
            var found = scratch.Found.Take(scratch.Count).ToList();
            Assert.Equal(found.Count, found.Distinct().Count());
            for (var r = 0; r < receivers.Count; r++)
                if (EnergyRayTracer.SphereEntry(start, direction, length, receivers[r], radius) >= 0)
                    Assert.Contains(r, found);
        }
    }

    private const int ReceiverIndexThreshold = 24;
}
