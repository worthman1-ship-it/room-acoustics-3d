using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>A long ray trace can report how far it has got and can be cancelled, without changing what it calculates.</summary>
public class RayTraceProgressTests
{
    private static readonly Vec3 Source = new(1.5, 1.2, 1.5);
    private static readonly Vec3 Receiver = new(4.2, 2.6, 1.2);

    private static Room3D Box() => Room3D.Shoebox(6, 4, 3, new SurfaceFinish(AbsorptionMaterial.Presets.HardReflective, OctaveSpectrum.Flat(0.2)));

    [Fact]
    public void Progress_RisesToOne_AndNeverGoesBeyondIt()
    {
        var seen = new List<double>();
        var gate = new object();
        var options = new RayTraceOptions(RayCount: 4000, Progress: p => { lock (gate) seen.Add(p); });

        EnergyRayTracer.Trace(Box(), Source, Receiver, options);

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.InRange(p, 0, 1.0000001));
        Assert.Equal(1.0, seen.Max(), 6);
        Assert.True(seen.Count >= 8, "at least one report for each of the eight bands");
    }

    [Fact]
    public void WatchingTheProgress_DoesNotChangeTheResult()
    {
        var plain = EnergyRayTracer.Trace(Box(), Source, Receiver, new RayTraceOptions(RayCount: 4000, Seed: 7));
        var watched = EnergyRayTracer.Trace(Box(), Source, Receiver, new RayTraceOptions(RayCount: 4000, Seed: 7, Progress: _ => { }, Cancellation: new CancellationTokenSource().Token));

        for (var band = 0; band < 8; band++)
            Assert.Equal(plain.Energy[band], watched.Energy[band]);
    }

    [Fact]
    public void ACancelledTrace_StopsWithAnOperationCanceledException()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            EnergyRayTracer.Trace(Box(), Source, Receiver, new RayTraceOptions(RayCount: 200_000, Cancellation: cancel.Token)));
    }

    [Fact]
    public void ATraceCancelledPartWay_StopsBeforeFinishing()
    {
        using var cancel = new CancellationTokenSource();
        var reports = 0;
        var options = new RayTraceOptions(RayCount: 20_000, Cancellation: cancel.Token, Progress: _ =>
        {
            // cancel once the first band is under way
            if (Interlocked.Increment(ref reports) == 3) cancel.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => EnergyRayTracer.Trace(Box(), Source, Receiver, options));
        Assert.True(reports < 8 * 32, "it must not have gone on through every chunk of every band");
    }
}
