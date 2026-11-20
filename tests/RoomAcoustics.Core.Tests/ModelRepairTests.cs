using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class ModelRepairTests
{
    private static Vec3 P(double x, double y, double z) => new(x, y, z);

    private static Room3D Room(SurfaceMesh mesh) => mesh.ToRoom(_ => Phase5Fixtures.Finish(0.1), SurfaceMesh.LooksLikeScreen);

    /// <summary>A closed 4 x 3 x 2.5 m box, with the faces <paramref name="skip"/> names left out.</summary>
    private static SurfaceMeshBuilder Box(string? skip = null)
    {
        var b = new SurfaceMeshBuilder();
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 3, 0), P(0, 3, 0), "Floor");
        if (skip != "Ceiling") b.AddQuad(P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(0, 3, 2.5), "Ceiling");
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 0, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        b.AddQuad(P(4, 3, 0), P(0, 3, 0), P(0, 3, 2.5), P(4, 3, 2.5), "Walls");
        b.AddQuad(P(0, 3, 0), P(0, 0, 0), P(0, 0, 2.5), P(0, 3, 2.5), "Walls");
        return b;
    }

    /// <summary>A closed solid: a block with a sloping top (two triangles more than a box), standing in the room.</summary>
    private static void Chair(SurfaceMeshBuilder b, double x, double y)
    {
        Vec3[] lo = { P(x, y, 0), P(x + 0.5, y, 0), P(x + 0.5, y + 0.5, 0), P(x, y + 0.5, 0) };
        Vec3[] hi = { P(x, y, 0.45), P(x + 0.5, y, 0.45), P(x + 0.5, y + 0.5, 0.9), P(x, y + 0.5, 0.9) };
        b.AddQuad(lo[0], lo[1], lo[2], lo[3], "Furniture");
        b.AddTriangle(hi[0], hi[1], hi[2], "Furniture");
        b.AddTriangle(hi[0], hi[2], hi[3], "Furniture");
        for (var i = 0; i < 4; i++) b.AddQuad(lo[i], lo[(i + 1) % 4], hi[(i + 1) % 4], hi[i], "Furniture");
    }

    [Fact]
    public void AClosedRoom_IsLeftAsItWas()
    {
        var mesh = Box().Build();
        var (repaired, steps) = ModelRepair.Repair(mesh);
        Assert.Empty(steps);
        Assert.Equal(mesh.Triangles.Count, repaired.Triangles.Count);
    }

    [Fact]
    public void AMissingCeiling_IsFilled_AndFurnitureAndALooseSheetBecomeScreens()
    {
        var b = Box(skip: "Ceiling");
        Chair(b, 1, 1);
        b.AddQuad(P(2.5, 1, 0.75), P(3.5, 1, 0.75), P(3.5, 2, 0.75), P(2.5, 2, 0.75), "Table top");
        var (repaired, steps) = ModelRepair.Repair(b.Build());

        Assert.Equal(30, Room(repaired).VolumeM3, 6);
        Assert.Contains("Filled ceiling", repaired.Groups);
        Assert.Contains("Furniture screen", repaired.Groups);
        Assert.Contains("Table top screen", repaired.Groups);
        Assert.Equal(10, repaired.Triangles.Count(t => t.Group == "Furniture screen")); // kept as drawn, less its base on the floor
        Assert.Equal(2, steps.Count);
        Assert.Contains("1 solid", steps[1]);
        Assert.Contains("1 sheet", steps[1]);
        Assert.Contains("leaving out 2 of their faces that lay on the room's surface", steps[1]);
    }

    [Fact]
    public void WithBoxes_ASolidBecomesTheBoxRoundIt_LessItsBase()
    {
        var b = Box();
        Chair(b, 1, 1);
        var (repaired, _) = ModelRepair.Repair(b.Build(), boxes: true);
        var box = repaired.Triangles.Where(t => t.Group == "Furniture screen").SelectMany(t => new[] { t.A, t.B, t.C }).Select(i => repaired.Vertices[i]).ToList();

        Assert.Equal(10, repaired.Triangles.Count(t => t.Group == "Furniture screen")); // the box, open where it stands on the floor
        Assert.Equal((1.0, 1.5, 1.0, 1.5, 0.0, 0.9), (box.Min(v => v.X), box.Max(v => v.X), box.Min(v => v.Y), box.Max(v => v.Y), box.Min(v => v.Z), box.Max(v => v.Z)));
        Assert.Equal(30, Room(repaired).VolumeM3, 6);
    }

    [Fact]
    public void CornersThatMissByAMillimetre_AreJoined_AndTheHairlineFaceBetweenThemGoes()
    {
        // the back wall's top corner drawn 1 mm short of the ceiling's, with a hairline face across the gap
        var b = new SurfaceMeshBuilder();
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 3, 0), P(0, 3, 0), "Floor");
        b.AddQuad(P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(0, 3, 2.5), "Ceiling");
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 0, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        b.AddQuad(P(4, 3, 0), P(0, 3, 0), P(0, 3, 2.499), P(4, 3, 2.5), "Walls");
        b.AddTriangle(P(0, 3, 2.499), P(0, 3, 2.5), P(4, 3, 2.5), "Walls");
        b.AddQuad(P(0, 3, 0), P(0, 0, 0), P(0, 0, 2.5), P(0, 3, 2.5), "Walls");
        var mesh = b.Build();
        Assert.NotEqual(0, MeshCheck.Check(mesh).OpenEdges + MeshCheck.Check(mesh).CrowdedEdges);

        var (repaired, steps) = ModelRepair.Repair(mesh);
        Assert.Contains(steps, s => s.StartsWith("Joined 1 corner"));
        Assert.Equal(30, Room(repaired).VolumeM3, 4);
    }

    [Fact]
    public void AnEdgeWithACornerAlongIt_IsCutThere()
    {
        // the floor drawn in two halves, the front and back walls each as one face, so each wall's bottom edge has a corner of the floor's along it
        var b = new SurfaceMeshBuilder();
        b.AddQuad(P(0, 0, 0), P(2, 0, 0), P(2, 3, 0), P(0, 3, 0), "Floor");
        b.AddQuad(P(2, 0, 0), P(4, 0, 0), P(4, 3, 0), P(2, 3, 0), "Floor");
        b.AddQuad(P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(0, 3, 2.5), "Ceiling");
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 0, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        b.AddQuad(P(4, 3, 0), P(0, 3, 0), P(0, 3, 2.5), P(4, 3, 2.5), "Walls");
        b.AddQuad(P(0, 3, 0), P(0, 0, 0), P(0, 0, 2.5), P(0, 3, 2.5), "Walls");
        var (repaired, steps) = ModelRepair.Repair(b.Build());

        Assert.Equal(new[] { "Cut 2 open edges at a corner lying along them (a T-junction)." }, steps);
        Assert.Equal(30, Room(repaired).VolumeM3, 6);
    }

    [Fact]
    public void AGalleryDrawnAsOneSheet_BecomesAScreen()
    {
        // walls drawn in two bands, split at the gallery's line: the gallery meets three walls (three faces on those edges) and is open along its front
        var b = new SurfaceMeshBuilder();
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 3, 0), P(0, 3, 0), "Room");
        b.AddQuad(P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(0, 3, 2.5), "Room");
        foreach (var (z0, z1) in new[] { (0.0, 1.2), (1.2, 2.5) })
        {
            b.AddQuad(P(0, 0, z0), P(4, 0, z0), P(4, 0, z1), P(0, 0, z1), "Room");
            b.AddQuad(P(0, 3, z0), P(4, 3, z0), P(4, 3, z1), P(0, 3, z1), "Room");
            foreach (var x in new[] { 0.0, 4.0 })
            {
                b.AddQuad(P(x, 0, z0), P(x, 2, z0), P(x, 2, z1), P(x, 0, z1), "Room");
                b.AddQuad(P(x, 2, z0), P(x, 3, z0), P(x, 3, z1), P(x, 2, z1), "Room");
            }
        }
        b.AddQuad(P(0, 2, 1.2), P(4, 2, 1.2), P(4, 3, 1.2), P(0, 3, 1.2), "Room");
        var (repaired, steps) = ModelRepair.Repair(b.Build());

        Assert.Equal(2, repaired.Triangles.Count(t => t.Group == "Room screen"));
        Assert.Contains(steps, s => s.StartsWith("Made 1 flat sheet hanging"));
        Assert.Equal(30, Room(repaired).VolumeM3, 6);
    }

    [Fact]
    public void AFlatSurfaceRoundAHoleThatCannotBeFilled_IsNotTakenForAHangingSheet()
    {
        // the floor in four quarters with two opposite ones missing: the two holes touch at the middle, so neither can be followed round and filled;
        // the floor left is open along them, but it meets the walls as a floor does, so it is no sheet hanging in the room
        var b = new SurfaceMeshBuilder();
        b.AddQuad(P(2, 0, 0), P(4, 0, 0), P(4, 1.5, 0), P(2, 1.5, 0), "Floor");
        b.AddQuad(P(0, 1.5, 0), P(2, 1.5, 0), P(2, 3, 0), P(0, 3, 0), "Floor");
        b.AddQuad(P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(0, 3, 2.5), "Ceiling");
        b.AddQuad(P(0, 0, 0), P(2, 0, 0), P(2, 0, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(2, 0, 0), P(4, 0, 0), P(4, 0, 2.5), P(2, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 1.5, 0), P(4, 1.5, 2.5), P(4, 0, 2.5), "Walls");
        b.AddQuad(P(4, 1.5, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 1.5, 2.5), "Walls");
        b.AddQuad(P(4, 3, 0), P(2, 3, 0), P(2, 3, 2.5), P(4, 3, 2.5), "Walls");
        b.AddQuad(P(2, 3, 0), P(0, 3, 0), P(0, 3, 2.5), P(2, 3, 2.5), "Walls");
        b.AddQuad(P(0, 3, 0), P(0, 1.5, 0), P(0, 1.5, 2.5), P(0, 3, 2.5), "Walls");
        b.AddQuad(P(0, 1.5, 0), P(0, 0, 0), P(0, 0, 2.5), P(0, 1.5, 2.5), "Walls");
        var (repaired, steps) = ModelRepair.Repair(b.Build());

        Assert.True(!repaired.Groups.Any(g => g.EndsWith(" screen")), string.Join(" | ", steps));
        Assert.Equal(4, repaired.Triangles.Count(t => t.Group == "Floor"));
    }
}
