using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class CornerDiffractionTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    /// <summary>An L-shaped room 3 m high: a 10 x 4 m arm along x and a 4 x 10 m arm along y, its re-entrant corner at (4, 4).</summary>
    private static Room3D LRoom(double alpha) =>
        Room3D.Extrude(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 4.0), (4.0, 4.0), (4.0, 10.0), (0.0, 10.0) }, 3, Finish(alpha), Finish(alpha), Finish(alpha));

    private static readonly Vec3 Source = new(8, 2, 1.5), Hidden = new(2, 8, 1.5);

    [Fact]
    public void AnLShapedRoom_HasOneEdgeToBendRound_ItsReEntrantCorner_AndABoxHasNone()
    {
        var edges = new CornerDiffraction(LRoom(0.2)).Edges;
        var edge = Assert.Single(edges);
        Assert.Equal(4, edge.A.X, 9); Assert.Equal(4, edge.A.Y, 9);
        Assert.Equal(4, edge.B.X, 9); Assert.Equal(4, edge.B.Y, 9);
        Assert.Equal(3, Math.Abs(edge.B.Z - edge.A.Z), 9);
        Assert.True(edge.Out.X < -0.7 && edge.Out.Y < -0.7, "out of the corner, into the room (towards (3, 3); beyond the corner, x and y over 4, is outside)");
        Assert.Empty(new CornerDiffraction(Room3D.Shoebox(6, 4, 3, Finish(0.2))).Edges);
    }

    [Fact]
    public void AReceiverHiddenRoundTheCorner_IsReachedOverTheCorner_ButOneInSightNeedsNoBend()
    {
        var corners = new CornerDiffraction(LRoom(0.2));
        var path = Assert.Single(corners.Paths(Source, Hidden));
        Assert.Equal(4, path.Turn.X, 2); Assert.Equal(4, path.Turn.Y, 2); Assert.Equal(1.5, path.Turn.Z, 2);
        var straight = (Hidden - Source).Length;
        var round = (new Vec3(4, 4, 1.5) - Source).Length + (Hidden - new Vec3(4, 4, 1.5)).Length;
        Assert.Equal(round, path.LengthM, 2);
        Assert.Equal(round - straight, path.ExtraM, 2);
        Assert.Empty(corners.Paths(Source, new Vec3(2, 2, 1.5)));
    }

    [Fact]
    public void InARoomThatReflectsNothing_TheHiddenReceiverHearsTheBentSound_AtItsTime_CutMoreAtHighFrequencies()
    {
        var room = LRoom(1);
        var options = new RayTraceOptions(RayCount: 500, ImageSourceOrder: 0);
        var bent = EnergyRayTracer.Trace(room, Source, Hidden, options);
        var plain = EnergyRayTracer.Trace(room, Source, Hidden, options with { CornerDiffraction = false });

        var path = new CornerDiffraction(room).Paths(Source, Hidden)[0];
        var bin = (int)(path.LengthM / EnergyRayTracer.SpeedOfSound(20) / options.BinWidthS);
        Assert.Equal(0, plain.Energy[4].Sum());                 // no direct sound round a corner without it, and nothing reflected
        Assert.True(bent.Energy[4][bin] > 0);
        Assert.Equal(bent.Energy[4][bin], bent.Energy[4].Sum(), 12);
        Assert.True(bent.Energy[1][bin] > 3 * bent.Energy[6][bin], "125 Hz bends round more easily than 4 kHz");
        Assert.Equal(0, bent.DirectEnergy!.Value[4]);            // it is not the direct sound
    }

    private static int WallAtXZero(Room3D room) =>
        Enumerable.Range(0, room.Faces.Count).Single(f => room.Faces[f].InwardNormal.X > 0.99 && room.Faces[f].Vertices.All(v => Math.Abs(v.X) < 1e-9));

    [Fact]
    public void AReflectionTheCornerHides_BendsRoundIt_OnTheLegTheCornerHidesItOn()
    {
        // Off the far wall x = 0 the specular path to the hidden receiver is cut by the corner on its way to the wall (the source to (0, 6.8));
        // it gets there by bending round the corner first, then reflecting at (0, 6.67): the receiver's image (-2, 8) lies in the corner's shadow.
        var room = LRoom(0.2);
        var wall = WallAtXZero(room);
        Assert.DoesNotContain(ImageSourceEnumerator.Enumerate(room, Source, Hidden, 1), a => a.Faces.SequenceEqual(new[] { wall }));

        var bent = ImageSourceEnumerator.EnumerateRoundCorners(room, Source, Hidden, 1, new CornerDiffraction(room), withPaths: true);
        var off = Assert.Single(bent, a => a.Faces.SequenceEqual(new[] { wall }));
        var path = off.Path!;
        Assert.Equal(3, path.Count);                              // the turn, the reflection, the receiver
        Assert.Equal(4, path[0].X, 2); Assert.Equal(4, path[0].Y, 2); Assert.Equal(1.5, path[0].Z, 2);
        Assert.Equal(0, path[1].X, 6); Assert.Equal(4 + 8.0 / 3, path[1].Y, 2);
        Assert.Equal(Hidden, path[2]);
        var turn = new Vec3(4, 4, 1.5);
        var route = (turn - Source).Length + (new Vec3(0, 4 + 8.0 / 3, 1.5) - turn).Length + (Hidden - new Vec3(0, 4 + 8.0 / 3, 1.5)).Length;
        Assert.Equal(route, off.DistanceM, 2);
        Assert.Equal(route - (new Vec3(-2, 8, 1.5) - Source).Length, off.Bend!.ExtraM, 2);
        Assert.Equal(turn.X, off.FirstReflection.X, 2);           // the source sends it towards the corner
        Assert.True(off.FinalDirection.X > 0 && off.FinalDirection.Y > 0, "it arrives from the wall, heading +x and +y");

        // every bent path turns at the corner, and none has a specular path of its own (the corner does not hide the reflection's other leg from it)
        Assert.All(bent, a => Assert.NotNull(a.Bend));
        Assert.All(bent, a => Assert.True(Math.Abs(a.Bend!.Turn.X - 4) < 0.01 && Math.Abs(a.Bend.Turn.Y - 4) < 0.01));
        var specular = ImageSourceEnumerator.Enumerate(room, Source, Hidden, 1).Select(a => a.Faces[0]).ToHashSet();
        Assert.DoesNotContain(bent, a => specular.Contains(a.Faces[0]));
    }

    [Fact]
    public void AReflectionTheCornerDoesNotHide_IsNotBent_WhenItsReflectedLegIsInSight()
    {
        // reflecting off x = 0 first, at (0, 3.33), the wall point sees the receiver straight: the corner does not shadow it, so there is no bent path
        // turning after the reflection
        var room = LRoom(0.2);
        var wall = WallAtXZero(room);
        var bent = ImageSourceEnumerator.EnumerateRoundCorners(room, Source, Hidden, 1, new CornerDiffraction(room), withPaths: true);
        Assert.DoesNotContain(bent, a => a.Faces.SequenceEqual(new[] { wall }) && a.Path![^1] != Hidden);
        Assert.DoesNotContain(bent, a => a.Faces.SequenceEqual(new[] { wall }) && Math.Abs(a.Path![1].Y - 10.0 / 3) < 0.05);
    }

    [Fact]
    public void TheTracer_AddsTheBentReflections_ToTheHiddenReceiver_EachCutByItsBend()
    {
        var room = LRoom(0.5);
        var options = new RayTraceOptions(RayCount: 300, ImageSourceOrder: 1, EdgeDiffraction: false);
        var on = EnergyRayTracer.Trace(room, Source, Hidden, options);
        var off = EnergyRayTracer.Trace(room, Source, Hidden, options with { CornerDiffraction = false });

        var corners = new CornerDiffraction(room);
        var bent = ImageSourceEnumerator.EnumerateRoundCorners(room, Source, Hidden, 1, corners);
        Assert.NotEmpty(bent);
        for (var band = 0; band < 8; band++)
        {
            var frequency = OctaveSpectrum.Frequencies[band];
            var wavelength = EnergyRayTracer.SpeedOfSound(20) / frequency;
            var air = AirAbsorption.AttenuationDbPerMetre(frequency, AirAbsorption.ReferencePressurePa, 20, options.RelativeHumidityPercent);
            var alpha = EnergyRayTracer.ReflectionLoss(room, band);
            var scatter = room.Faces.Select(f => f.Finish.ScatteringCoefficients[band]).ToArray();
            var expected = corners.Paths(Source, Hidden).Sum(p =>
                               EnergyRayTracer.SphereFraction(p.LengthM, options.ReceiverRadiusM) * Math.Pow(10, -air * p.LengthM / 10) * CornerDiffraction.Factor(p, wavelength))
                           + bent.Sum(a => EnergyRayTracer.SpecularPathEnergy(a, Source, alpha, scatter, options.ReceiverRadiusM, air, SourceDirectivity.Omnidirectional, room, wavelength, band));
            Assert.Equal(expected, on.Energy[band].Sum() - off.Energy[band].Sum(), 12);
        }
        var x0 = bent.Single(a => a.Faces.SequenceEqual(new[] { WallAtXZero(room) }));
        Assert.True(EnergyRayTracer.SpecularPathEnergy(x0, Source, EnergyRayTracer.ReflectionLoss(room, 1), new double[room.Faces.Count], options.ReceiverRadiusM, 0,
                        SourceDirectivity.Omnidirectional, room, EnergyRayTracer.SpeedOfSound(20) / 125, 1)
                    > 3 * EnergyRayTracer.SpecularPathEnergy(x0, Source, EnergyRayTracer.ReflectionLoss(room, 6), new double[room.Faces.Count], options.ReceiverRadiusM, 0,
                        SourceDirectivity.Omnidirectional, room, EnergyRayTracer.SpeedOfSound(20) / 4000, 6),
                    "the bend cuts 4 kHz more than 125 Hz");
    }
}
