using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The direct sound on its own: its strength, and the direct-to-reverberant ratio.</summary>
public class DirectSoundTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()));

    private static readonly RayTraceOptions Options = new(RayCount: 4_000, ImageSourceOrder: 1);

    [Fact]
    public void TheDirectSoundsStrength_IsTheFreeFieldLevelAgainst10m()
    {
        var room = Room3D.Shoebox(20, 12, 6, Finish(0.3));
        var m = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(room, new Vec3(5, 6, 1.5), new Vec3(10, 6, 1.5), Options));
        var direct = Assert.NotNull(m.DirectStrengthGDb);
        // 20 lg(10 / 5) = 6.02 dB, less the air's absorption over 5 m less than 10 m (small below 4 kHz)
        for (var b = 0; b < 6; b++) Assert.InRange(direct[b], 6.0, 6.1);
        Assert.True(direct[7] > direct[4], "air takes more over 10 m than 5 m at 8 kHz");
    }

    [Fact]
    public void TheDirectToReverberantRatio_IsTheDirectSoundAgainstTheRestOfG()
    {
        var room = Room3D.Shoebox(20, 12, 6, Finish(0.3));
        var m = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(room, new Vec3(5, 6, 1.5), new Vec3(10, 6, 1.5), Options));
        for (var b = 0; b < 8; b++)
        {
            var g = m.StrengthGDb[b];
            var gd = m.DirectStrengthGDb!.Value[b];
            var reverberant = 10 * Math.Log10(Math.Pow(10, g / 10) - Math.Pow(10, gd / 10));
            Assert.Equal(gd - reverberant, m.DirectToReverberantDb!.Value[b], 6);
        }
        // a softer room has less reverberant sound for the same direct sound
        var soft = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(Room3D.Shoebox(20, 12, 6, Finish(0.8)), new Vec3(5, 6, 1.5), new Vec3(10, 6, 1.5), Options));
        Assert.True(soft.DirectToReverberantDb!.Value[4] > m.DirectToReverberantDb!.Value[4] + 3);
        var broadband = BroadbandMetrics.From(m);
        Assert.Equal((m.DirectToReverberantDb.Value[3] + m.DirectToReverberantDb.Value[4]) / 2, broadband.DirectToReverberantDb, 9);
    }

    [Fact]
    public void AWallBetween_LeavesNoDirectSound()
    {
        // an L-shaped room: round the corner the walls block the line between them
        var l = Room3D.Extrude(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 4.0), (4.0, 4.0), (4.0, 10.0), (0.0, 10.0) }, 3, Finish(0.2), Finish(0.2), Finish(0.2));
        var m = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(l, new Vec3(8, 2, 1.5), new Vec3(2, 8, 1.5), Options));
        Assert.All(Enumerable.Range(0, 8), b => Assert.True(double.IsNaN(m.DirectStrengthGDb!.Value[b])));
        Assert.All(Enumerable.Range(0, 8), b => Assert.True(double.IsNaN(m.DirectToReverberantDb!.Value[b])));
        Assert.True(double.IsFinite(m.StrengthGDb[4]), "there is still sound round the corner");
    }

    [Fact]
    public void TwoElements_AddTheirDirectSoundsByTheirShares()
    {
        var room = Room3D.Shoebox(20, 12, 6, Finish(0.3));
        Vec3 a = new(5, 4, 1.5), b = new(5, 8, 1.5), receiver = new(12, 6, 1.5);
        var both = ExtendedSourceTracer.TraceReceivers(room, new ExtendedSource(new[] { new SourcePoint(a), new SourcePoint(b) }), new[] { receiver }, Options)[0];
        var one = EnergyRayTracer.Trace(room, a, receiver, Options);
        // the two are the same distance away, each with half the power: together, as much direct sound as one at full power
        for (var band = 0; band < 8; band++) Assert.Equal(one.DirectEnergy!.Value[band], both.DirectEnergy!.Value[band], 12);
    }

    [Fact]
    public void AMeasuredResponse_DoesNotKnowItsDirectSound()
    {
        var ir = new ImpulseResponse(0.001, Enumerable.Range(0, 8).Select(_ => new double[] { 1, 0.5, 0.25 }).ToArray(), OctaveSpectrum.Flat(1));
        var m = RoomAcousticsCalculator.Calculate(ir);
        Assert.Null(m.DirectStrengthGDb);
        Assert.Null(m.DirectToReverberantDb);
        Assert.True(double.IsNaN(BroadbandMetrics.From(m).DirectStrengthGDb));
    }
}
