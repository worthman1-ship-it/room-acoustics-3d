using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class SurfaceGridTests
{
    private static SurfaceFinish Finish() => new(AbsorptionMaterial.Presets.All[0], OctaveSpectrum.Flat(0.1));

    /// <summary>A room 10 m long and 6 m wide whose floor rises 0.2 m for every metre along x (to 2 m at the back) under a flat ceiling at 6 m.</summary>
    private static Room3D RakedRoom() => new(new[]
    {
        new PlanarFace("Raked floor 1", new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 2), new Vec3(10, 6, 2), new Vec3(0, 6, 0) }, Finish()),
        new PlanarFace("Ceiling 1", new[] { new Vec3(0, 0, 6), new Vec3(10, 0, 6), new Vec3(10, 6, 6), new Vec3(0, 6, 6) }, Finish()),
        new PlanarFace("Wall 1", new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 2), new Vec3(10, 0, 6), new Vec3(0, 0, 6) }, Finish()),
        new PlanarFace("Wall 2", new[] { new Vec3(0, 6, 0), new Vec3(10, 6, 2), new Vec3(10, 6, 6), new Vec3(0, 6, 6) }, Finish()),
        new PlanarFace("Wall 3", new[] { new Vec3(0, 0, 0), new Vec3(0, 6, 0), new Vec3(0, 6, 6), new Vec3(0, 0, 6) }, Finish()),
        new PlanarFace("Wall 4", new[] { new Vec3(10, 0, 2), new Vec3(10, 6, 2), new Vec3(10, 6, 6), new Vec3(10, 0, 6) }, Finish()),
    });

    [Fact]
    public void OverSurface_OnARakedFloor_PutsEveryReceiverTheSameHeightAboveTheFloorSoTheGridSlopes()
    {
        var points = ReceiverGrid.OverSurface(RakedRoom(), n => n.StartsWith("Raked floor"), 1.2, 1.0, 0.5);

        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Equal(0.2 * p.X + 1.2, p.Z, 9));
        Assert.True(points.Max(p => p.Z) - points.Min(p => p.Z) > 1.0, "the grid should climb with the floor");
    }

    [Fact]
    public void OverSurface_LeavesOutCellsWithNoMatchingSurfaceBeneath()
    {
        // a floor in two parts, 0-5 m and 5-10 m along x: asking for the first part only gives receivers over that part
        var faces = new List<PlanarFace>
        {
            new("Floor A 1", new[] { new Vec3(0, 0, 0), new Vec3(5, 0, 0), new Vec3(5, 7, 0), new Vec3(0, 7, 0) }, Finish()),
            new("Floor B 1", new[] { new Vec3(5, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 7, 0), new Vec3(5, 7, 0) }, Finish()),
        };
        // the ceiling and the long walls are cut at x = 5 too, so no corner lands along another face's edge
        faces.Add(new("Ceiling A 1", new[] { new Vec3(0, 0, 3), new Vec3(5, 0, 3), new Vec3(5, 7, 3), new Vec3(0, 7, 3) }, Finish()));
        faces.Add(new("Ceiling B 1", new[] { new Vec3(5, 0, 3), new Vec3(10, 0, 3), new Vec3(10, 7, 3), new Vec3(5, 7, 3) }, Finish()));
        foreach (var y in new[] { 0.0, 7.0 })
            foreach (var (x0, x1) in new[] { (0.0, 5.0), (5.0, 10.0) })
                faces.Add(new($"Wall {y}-{x0} 1", new[] { new Vec3(x0, y, 0), new Vec3(x1, y, 0), new Vec3(x1, y, 3), new Vec3(x0, y, 3) }, Finish()));
        faces.Add(new("End 1", new[] { new Vec3(0, 0, 0), new Vec3(0, 7, 0), new Vec3(0, 7, 3), new Vec3(0, 0, 3) }, Finish()));
        faces.Add(new("End 2", new[] { new Vec3(10, 0, 0), new Vec3(10, 7, 0), new Vec3(10, 7, 3), new Vec3(10, 0, 3) }, Finish()));
        var room = new Room3D(faces);

        var points = ReceiverGrid.OverSurface(room, n => n.StartsWith("Floor A"), 1.2, 1.0, 0.5);

        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.InRange(p.X, 0, 5));
    }

    [Fact]
    public void OverSurface_WithNoUpwardFacingSurfaceOfThatName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ReceiverGrid.OverSurface(RakedRoom(), n => n.StartsWith("Ceiling"), 1.2, 1.0, 0.5));
        Assert.Contains("faces up", ex.Message);
    }
}
