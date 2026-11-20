using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>Free-standing screens: two-sided faces inside the room that reflect from either side, block lines of sight and let sound past by their free edges.</summary>
public class ScreenTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()));

    /// <summary>A 10 x 6 x 3 m room with a 1.5 m high screen standing on the floor across the middle (x = 5), 1 m short of each side wall.</summary>
    private static Room3D Office(double screenAlpha = 0.1, double roomAlpha = 0.1)
    {
        var box = Room3D.Shoebox(10, 6, 3, Finish(roomAlpha));
        var screen = new PlanarFace("Screen", new[] { new Vec3(5, 1, 0), new Vec3(5, 5, 0), new Vec3(5, 5, 1.5), new Vec3(5, 1, 1.5) }, Finish(screenAlpha));
        return new Room3D(box.Faces.Select(f => new PlanarFace(f.Name, f.Vertices, f.Finish)), new[] { screen });
    }

    [Fact]
    public void TheKurzeAndersonLossIs5DbAtGrazingAndAbout13DbAtFresnelNumber1()
    {
        Assert.Equal(5, ScreenDiffraction.InsertionLossDb(0), 9);
        Assert.InRange(ScreenDiffraction.InsertionLossDb(1), 13.0, 13.2); // Maekawa's chart reads 13 dB
        Assert.True(ScreenDiffraction.InsertionLossDb(10) > ScreenDiffraction.InsertionLossDb(1));
    }

    [Fact]
    public void ThePathDifferenceIsTheShortestRouteOverTheEdge()
    {
        // a long top edge 1 m above the line between two points 4 m apart, half way: 2 √(2² + 1²) - 4
        var delta = ScreenDiffraction.PathDifference(new Vec3(0, 0, 1), new Vec3(4, 0, 1), new Vec3(2, -5, 2), new Vec3(2, 5, 2));
        Assert.Equal(2 * Math.Sqrt(5) - 4, delta, 9);
        // an edge ending short of the line is reached at its end
        var end = ScreenDiffraction.PathDifference(new Vec3(0, 0, 1), new Vec3(4, 0, 1), new Vec3(2, 1, 1), new Vec3(2, 5, 1));
        Assert.Equal(2 * Math.Sqrt(5) - 4, end, 9);
    }

    [Fact]
    public void AScreenIsInsideTheRoomButNotPartOfItsVolume()
    {
        var room = Office();
        Assert.Equal(7, room.Faces.Count);
        Assert.Equal(6, room.BoundaryFaceCount);
        Assert.True(room.Faces[6].IsScreen);
        Assert.Equal(180, room.VolumeM3, 9);
        Assert.Equal(2 * (60 + 30 + 18), room.TotalAreaM2, 9);
        Assert.True(room.Contains(new Vec3(5, 3, 0.5))); // a point on the screen is still in the room
        // its bottom edge stands on the floor, so sound passes only over the top and round the two sides
        var panel = Assert.Single(room.Screens);
        Assert.Equal(3, panel.Edges.Count);
        Assert.DoesNotContain(panel.Edges, e => e.A.Z == 0 && e.B.Z == 0);
    }

    [Fact]
    public void AScreenBlocksTheLineOfSightButNotAsAWall()
    {
        var room = Office();
        Vec3 a = new(2, 3, 1.2), b = new(8, 3, 1.2);
        Assert.False(room.IsClear(a, b));
        Assert.True(room.IsClearOfWalls(a, b));
        Assert.Equal(new[] { 0 }, room.ScreensCrossed(a, b));
        Assert.Empty(room.ScreensCrossed(a, new Vec3(4, 3, 1.2)));
        Assert.Empty(room.ScreensCrossed(new Vec3(2, 3, 2), new Vec3(8, 3, 2))); // over the top
        Assert.Equal(0.5, room.DistanceToBoundary(new Vec3(5.5, 3, 1)), 9);   // a receiver must keep clear of a screen too
    }

    [Fact]
    public void AScreenThatIsNotInTheRoomIsRefused()
    {
        var box = Room3D.Shoebox(10, 6, 3, Finish(0.1));
        var outside = new PlanarFace("Screen", new[] { new Vec3(12, 1, 0), new Vec3(12, 5, 0), new Vec3(12, 5, 1.5), new Vec3(12, 1, 1.5) }, Finish(0.1));
        var ex = Assert.Throws<ArgumentException>(() => new Room3D(box.Faces.Select(f => new PlanarFace(f.Name, f.Vertices, f.Finish)), new[] { outside }));
        Assert.Contains("not inside", ex.Message);
    }

    [Fact]
    public void TheDirectSoundBehindAScreenIsCutByWhatBendsRoundItsEdges()
    {
        // every surface absorbs all it is given, so the direct sound's time bin holds nothing else (a ray counts as it enters the 0.5 m receiver sphere,
        // which puts the floor reflection in the same millisecond)
        var open = Room3D.Shoebox(10, 6, 3, Finish(1));
        var screened = Office(1, 1);
        Vec3 source = new(2, 3, 1.2), receiver = new(8, 3, 1.2);
        var options = new RayTraceOptions(RayCount: 2_000, ImageSourceOrder: 0);
        var without = EnergyRayTracer.Trace(open, source, receiver, options);
        var with = EnergyRayTracer.Trace(screened, source, receiver, options);

        var speed = EnergyRayTracer.SpeedOfSound(20);
        var bin = (int)(6 / speed / options.BinWidthS);
        for (var band = 0; band < 8; band++)
        {
            var expected = ScreenDiffraction.EnergyFactor(screened, source, receiver, speed / OctaveSpectrum.Frequencies[band]);
            Assert.Equal(expected, with.Energy[band][bin] / without.Energy[band][bin], 6);
            Assert.InRange(expected, 0.01 - 1e-12, 0.6); // at least about 2 dB, at most the 20 dB limit
        }
        // the cut grows with frequency: a short wavelength bends round less
        Assert.True(with.Energy[7][bin] / without.Energy[7][bin] < with.Energy[1][bin] / without.Energy[1][bin]);
    }

    [Fact]
    public void AScreenReflectsFromEitherSide_AsAnImageSourceAndForTheRays()
    {
        var room = Office();
        // both on the source side: the first-order reflection off the screen is the mirror image in x = 5
        Vec3 source = new(2, 3, 1.2), receiver = new(3, 4, 1.2);
        var arrivals = ImageSourceEnumerator.Enumerate(room, source, receiver, 1);
        var off = Assert.Single(arrivals, a => a.Faces[0] == 6);
        Assert.Equal((receiver - new Vec3(8, 3, 1.2)).Length, off.DistanceM, 9);
        // and from the far side
        var back = ImageSourceEnumerator.Enumerate(room, new Vec3(8, 3, 1.2), new Vec3(7, 2, 1.2), 1);
        Assert.Single(back, a => a.Faces[0] == 6);

        // a path that has to pass through the screen to a wall and back is still there, marked for the screen to cut
        var floor = ImageSourceEnumerator.Enumerate(room, source, new Vec3(9, 3, 1.2), 1).Single(a => room.Faces[a.Faces[0]].Name == "Floor");
        Assert.NotNull(floor.ScreenedLegs);

        // rays bounce off it: a hard screen keeps more energy on the source side than an absorbent one
        var options = new RayTraceOptions(RayCount: 4_000, ImageSourceOrder: 0, MaxTimeS: 0.5);
        var hard = EnergyRayTracer.Trace(Office(0.05), source, receiver, options).Energy[4].Sum();
        var soft = EnergyRayTracer.Trace(Office(0.95), source, receiver, options).Energy[4].Sum();
        Assert.True(hard > soft * 1.05);
    }

    [Fact]
    public void AModelLayerCanBeScreens()
    {
        var builder = new SurfaceMeshBuilder();
        foreach (var f in Room3D.Shoebox(10, 6, 3, Finish(0.1)).Faces)
            builder.AddQuad(f.Vertices[0], f.Vertices[1], f.Vertices[2], f.Vertices[3], "Walls");
        builder.AddQuad(new Vec3(5, 1, 0), new Vec3(5, 5, 0), new Vec3(5, 5, 1.5), new Vec3(5, 1, 1.5), "Desk screens");
        var mesh = builder.Build();

        Assert.Throws<ArgumentException>(() => mesh.ToRoom(_ => Finish(0.1))); // left as part of the closed surface it does not close
        var room = mesh.ToRoom(_ => Finish(0.1), SurfaceMesh.LooksLikeScreen);
        // its two triangles share an edge, so they are one panel, with the same three free edges as the drawn screen
        var panel = Assert.Single(room.Screens);
        Assert.Equal(2, panel.Faces.Count);
        Assert.Equal(3, panel.Edges.Count);
        Assert.Equal(180, room.VolumeM3, 6);
    }
}
