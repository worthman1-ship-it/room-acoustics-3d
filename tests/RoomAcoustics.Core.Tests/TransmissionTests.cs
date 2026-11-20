using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>Sound passing through a partition (a screen with a sound reduction), and two rooms coupled by it or by a door in it.</summary>
public class TransmissionTests
{
    private static SurfaceFinish Finish(double alpha, double? reductionDb = null) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0),
            reductionDb is { } r ? OctaveSpectrum.Flat(r) : null);

    private static Vec3 P(double x, double y, double z) => new(x, y, z);

    /// <summary>A 10 x 6 x 3 m room split at x = 5 by a partition from wall to wall and floor to ceiling, with a door (1 m wide, 2 m high, from y = 2.5) if asked.</summary>
    private static Room3D TwoRooms(double reductionDb, bool door = false, double alpha = 0.2)
    {
        var box = Room3D.Shoebox(10, 6, 3, Finish(alpha));
        var wall = Finish(alpha, reductionDb);
        PlanarFace Piece(double y0, double y1, double z0, double z1, int n) =>
            new($"Partition {n}", new[] { P(5, y0, z0), P(5, y1, z0), P(5, y1, z1), P(5, y0, z1) }, wall);
        var partition = door
            // round the door, cut so that the pieces meet edge to edge: below and above the door's head each side, and over the door
            ? new[] { Piece(0, 2.5, 0, 2, 1), Piece(0, 2.5, 2, 3, 2), Piece(2.5, 3.5, 2, 3, 3), Piece(3.5, 6, 2, 3, 4), Piece(3.5, 6, 0, 2, 5) }
            : new[] { Piece(0, 6, 0, 3, 1) };
        return new Room3D(box.Faces.Select(f => new PlanarFace(f.Name, f.Vertices, f.Finish)), partition);
    }

    private static readonly Vec3 Source = P(2, 3, 1.5);
    private static readonly Vec3[] SourceRoom = { P(1.5, 1.5, 1.2), P(3.5, 4.5, 1.8), P(4, 1.2, 1.2), P(1.2, 5, 2) };
    private static readonly Vec3[] ReceivingRoom = { P(6.5, 1.5, 1.2), P(8.5, 4.5, 1.8), P(7.5, 3, 1.5), P(9, 1.2, 2), P(6.2, 5, 1) };

    /// <summary>The mean energy over some receivers in the 1 kHz band.</summary>
    private static double MeanEnergy(IReadOnlyList<ImpulseResponse> responses, int from, int count) =>
        Enumerable.Range(from, count).Average(r => responses[r].Energy[4].Sum());

    private static IReadOnlyList<ImpulseResponse> Trace(Room3D room, int rays = 40_000) =>
        EnergyRayTracer.TraceReceivers(room, Source, SourceRoom.Concat(ReceivingRoom).ToList(), new RayTraceOptions(RayCount: rays, ImageSourceOrder: 2, MaxTimeS: 2));

    [Fact]
    public void ThroughAWallToWallPartition_TheDirectSoundIsCutByItsReduction()
    {
        var room = TwoRooms(25);
        Assert.Equal(Math.Pow(10, -2.5), ScreenDiffraction.EnergyFactor(room, Source, P(8, 3, 1.5), 0.34, 4), 12);
        Assert.Equal(0, ScreenDiffraction.EnergyFactor(room, Source, P(8, 3, 1.5), 0.34), 12); // with no band, nothing passes through: it has no free edge
        Assert.Equal(Math.Pow(10, -2.5), room.Faces[^1].Finish.Transmission(4), 12);
    }

    [Fact]
    public void ASolidPartition_LeavesTheOtherRoomSilent()
    {
        var responses = Trace(TwoRooms(200), 5_000);
        Assert.True(MeanEnergy(responses, 0, SourceRoom.Length) > 0);
        Assert.True(MeanEnergy(responses, SourceRoom.Length, ReceivingRoom.Length) < 1e-18, "not even by a wall reflection just where the partition meets the wall");
    }

    [Fact]
    public void TheLevelDifference_IsTheReductionPlusTheReceivingRoomsAbsorptionOverThePartition()
    {
        // D = R + 10 lg(A / S): the receiving room's absorption area A over the partition's area S, in diffuse fields
        const double r = 20;
        var responses = Trace(TwoRooms(r));
        var difference = 10 * Math.Log10(MeanEnergy(responses, 0, SourceRoom.Length) / MeanEnergy(responses, SourceRoom.Length, ReceivingRoom.Length));
        var absorptionArea = 0.2 * (2 * 5 * 6 + 2 * 5 * 3 + 6 * 3 + 6 * 3);
        var expected = r + 10 * Math.Log10(absorptionArea / 18);
        Assert.InRange(difference, expected - 2.5, expected + 2.5);
    }

    [Fact]
    public void ADoorInAHeavyPartition_CouplesTheRooms()
    {
        var shut = MeanEnergy(Trace(TwoRooms(60), 20_000), SourceRoom.Length, ReceivingRoom.Length);
        var open = Trace(TwoRooms(60, door: true), 20_000);
        var through = MeanEnergy(open, SourceRoom.Length, ReceivingRoom.Length);
        Assert.True(10 * Math.Log10(through / shut) > 20, "the door lets far more through than the wall");
        // the door is 2 m² of a 18 m² partition: the receiving room is well below the source room
        var difference = 10 * Math.Log10(MeanEnergy(open, 0, SourceRoom.Length) / through);
        Assert.InRange(difference, 5, 15);
    }

    [Fact]
    public void AScreenSoundPassesStraightThrough_IsAsIfItWereNotThere()
    {
        var box = Room3D.Shoebox(10, 6, 3, Finish(0.2));
        var glassless = new PlanarFace("Screen", new[] { P(5, 1, 0), P(5, 5, 0), P(5, 5, 2), P(5, 1, 2) }, Finish(0.2, 0));
        var screened = new Room3D(box.Faces.Select(f => new PlanarFace(f.Name, f.Vertices, f.Finish)), new[] { glassless });
        var options = new RayTraceOptions(RayCount: 20_000, ImageSourceOrder: 2);
        var without = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(box, Source, P(8, 3, 1.5), options));
        var with = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(screened, Source, P(8, 3, 1.5), options));
        for (var b = 2; b < 7; b++)
        {
            Assert.InRange(with.StrengthGDb[b] - without.StrengthGDb[b], -0.3, 0.3);
            Assert.Equal(without.DirectStrengthGDb!.Value[b], with.DirectStrengthGDb!.Value[b], 9);
        }
    }
}
