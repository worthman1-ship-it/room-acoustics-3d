using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class HoleFillTests
{
    private static Vec3 P(double x, double y, double z) => new(x, y, z);

    /// <summary>
    /// A 4 x 3 x 2.5 m box with no ceiling. With <paramref name="doorway"/> the front wall (y = 0) has a doorway from x = 1.5 to 2.5 open to the top;
    /// with <paramref name="noFrontWall"/> it is not there at all.
    /// </summary>
    private static SurfaceMeshBuilder OpenBox(bool doorway = false, bool noFrontWall = false)
    {
        var b = new SurfaceMeshBuilder();
        // the floor has corners at the doorway's sides, as the walls do
        foreach (var (x0, x1) in new[] { (0.0, 1.5), (1.5, 2.5), (2.5, 4.0) })
            b.AddTriangle(P(0, 3, 0), P(x0, 0, 0), P(x1, 0, 0), "Floor");
        b.AddTriangle(P(0, 3, 0), P(4, 0, 0), P(4, 3, 0), "Floor");
        if (!noFrontWall)
            foreach (var (x0, x1) in doorway ? new[] { (0.0, 1.5), (2.5, 4.0) } : new[] { (0.0, 1.5), (1.5, 2.5), (2.5, 4.0) })
                b.AddQuad(P(x0, 0, 0), P(x1, 0, 0), P(x1, 0, 2.5), P(x0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        b.AddQuad(P(4, 3, 0), P(0, 3, 0), P(0, 3, 2.5), P(4, 3, 2.5), "Walls");
        b.AddQuad(P(0, 3, 0), P(0, 0, 0), P(0, 0, 2.5), P(0, 3, 2.5), "Walls");
        return b;
    }

    private static Room3D Room(SurfaceMesh mesh) => mesh.ToRoom(_ => Phase5Fixtures.Finish(0.1));

    private static SurfaceMesh FillAll(SurfaceMesh mesh, out List<HoleCap> caps)
    {
        var filler = new HoleFill(mesh);
        caps = MeshCheck.Check(mesh).Faults.Where(f => f.Kind == MeshFaultKind.Hole).Select(f => filler.Plan(f.Edges)).OfType<IReadOnlyList<HoleCap>>()
            .SelectMany(p => p).ToList();
        return HoleFill.Fill(mesh, caps.Select((c, i) => (c, $"Filled {i + 1}")));
    }

    [Fact]
    public void AMissingCeiling_IsClosedByOneSurface()
    {
        var mesh = OpenBox().Build();
        var filled = FillAll(mesh, out var caps);

        var cap = Assert.Single(caps);
        Assert.Equal(12, cap.AreaM2, 9);
        Assert.Equal(1, Math.Abs(cap.Normal.Z), 9);
        Assert.Equal(2.5, cap.Centre.Z, 9);
        Assert.Equal(0, MeshCheck.Check(filled).OpenEdges);
        Assert.Equal(30, Room(filled).VolumeM3, 6);
        Assert.Equal(12, filled.GroupAreasM2()["Filled 1"], 9);
    }

    [Fact]
    public void ACeilingMissingOverADoorwayOpenToTheTop_IsClosedAsTheCeilingAndTheDoorway()
    {
        // the hole runs round the walls' tops and dips down the doorway: it is closed across the doorway's top, and the doorway by itself
        var filled = FillAll(OpenBox(doorway: true).Build(), out var caps);

        Assert.Equal(2, caps.Count);
        var ceiling = Assert.Single(caps, c => Math.Abs(c.Normal.Z) > 0.99);
        var door = Assert.Single(caps, c => Math.Abs(c.Normal.Y) > 0.99);
        Assert.Equal(12, ceiling.AreaM2, 9);
        Assert.Equal(2.5, door.AreaM2, 9);
        Assert.Equal(0, MeshCheck.Check(filled).OpenEdges);
        Assert.Equal(30, Room(filled).VolumeM3, 6);
    }

    [Fact]
    public void ACeilingMissingWithAWallUnderIt_IsClosedAsTheCeilingAndTheWall()
    {
        // the hole's own normal leans between the two; the wall's plane holds the most corners, and the ceiling is the stretch that leaves it
        var filled = FillAll(OpenBox(noFrontWall: true).Build(), out var caps);

        Assert.Equal(2, caps.Count);
        Assert.Equal(12, Assert.Single(caps, c => Math.Abs(c.Normal.Z) > 0.99).AreaM2, 9);
        Assert.Equal(10, Assert.Single(caps, c => Math.Abs(c.Normal.Y) > 0.99).AreaM2, 9);
        Assert.Equal(0, MeshCheck.Check(filled).OpenEdges);
        Assert.Equal(30, Room(filled).VolumeM3, 6);
    }

    [Fact]
    public void LooseTilesInTheMissingCeiling_BecomePartOfIt_AndAreNotHolesThemselves()
    {
        // ceiling tiles drawn as faces of their own where the ceiling should be: the ceiling is closed round them, and the tiles' outlines are refused
        var b = OpenBox();
        foreach (var (x, y) in new[] { (0.5, 0.5), (2.0, 0.5), (0.5, 1.8), (2.0, 1.8), (3.0, 1.0) })
            b.AddQuad(P(x, y, 2.5), P(x + 0.6, y, 2.5), P(x + 0.6, y + 0.6, 2.5), P(x, y + 0.6, 2.5), "Tiles");
        var mesh = b.Build();
        var filler = new HoleFill(mesh);
        var holes = MeshCheck.Check(mesh).Faults.Where(f => f.Kind == MeshFaultKind.Hole).ToList();
        Assert.Equal(6, holes.Count);
        Assert.All(holes.Where(h => h.Groups.SequenceEqual(new[] { "Tiles" })), h => Assert.Null(filler.Plan(h.Edges)));

        var filled = FillAll(mesh, out var caps);
        Assert.Equal(12 - 5 * 0.36, Assert.Single(caps).AreaM2, 9);
        Assert.Equal(0, MeshCheck.Check(filled).OpenEdges);
        Assert.Equal(30, Room(filled).VolumeM3, 6);
    }

    [Fact]
    public void ALooseSheetStandingInTheRoom_IsNotFilled()
    {
        // a table top drawn as one face: closing its outline would draw it twice
        var b = new SurfaceMeshBuilder();
        b.AddQuad(P(1, 1, 0.75), P(2.5, 1, 0.75), P(2.5, 1.7, 0.75), P(1, 1.7, 0.75), "Table");
        var mesh = b.Build();
        var hole = Assert.Single(MeshCheck.Check(mesh).Faults, f => f.Kind == MeshFaultKind.Hole);
        Assert.Null(new HoleFill(mesh).Plan(hole.Edges));
    }

    [Fact]
    public void APolygonWithManyHoles_IsCoveredExactly()
    {
        // a 10 m square with a 4 x 4 grid of 1 m holes: each hole's cut must go round the holes not yet joined, and leave a corner an earlier cut
        // doubled from the right side
        var square = new List<Vec3> { P(0, 0, 0), P(10, 0, 0), P(10, 10, 0), P(0, 10, 0) };
        var holes = new List<List<Vec3>>();
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                double x = 1 + 2.2 * i, y = 1 + 2.2 * j;
                holes.Add(new List<Vec3> { P(x, y, 0), P(x + 1, y, 0), P(x + 1, y + 1, 0), P(x, y + 1, 0) });
            }
        var triangles = Polygon.Triangulate(square, holes);
        Assert.Equal(100 - 16, triangles.Sum(t => (t[1] - t[0]).Cross(t[2] - t[0]).Length / 2), 6);
        Assert.All(triangles, t => Assert.True((t[1] - t[0]).Cross(t[2] - t[0]).Z > -1e-9, "every triangle keeps the outline's winding"));
    }
}
