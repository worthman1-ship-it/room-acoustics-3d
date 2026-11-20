using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class SurfaceSplitTests
{
    private static Vec3 P(double x, double y, double z) => new(x, y, z);

    /// <summary>A 4 x 3 x 2.5 m box all in one group, as SketchUp exports a model with no materials; each face drawn in two halves.</summary>
    private static SurfaceMeshBuilder Box(string group = "material")
    {
        var b = new SurfaceMeshBuilder();
        foreach (var (x0, x1) in new[] { (0.0, 2.0), (2.0, 4.0) })
        {
            b.AddQuad(P(x0, 0, 0), P(x1, 0, 0), P(x1, 3, 0), P(x0, 3, 0), group);
            b.AddQuad(P(x0, 0, 2.5), P(x1, 0, 2.5), P(x1, 3, 2.5), P(x0, 3, 2.5), group);
            b.AddQuad(P(x0, 0, 0), P(x1, 0, 0), P(x1, 0, 2.5), P(x0, 0, 2.5), group);
            b.AddQuad(P(x0, 3, 0), P(x1, 3, 0), P(x1, 3, 2.5), P(x0, 3, 2.5), group);
        }
        b.AddQuad(P(0, 0, 0), P(0, 3, 0), P(0, 3, 2.5), P(0, 0, 2.5), group);
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), group);
        return b;
    }

    [Fact]
    public void ABoxOnOneLayer_IsSplitIntoItsFloorCeilingAndWalls_NamedForWhereTheyLie()
    {
        var mesh = Box().Build();
        Assert.Equal(6, SurfaceSplit.Count(mesh, "material"));

        var split = SurfaceSplit.Split(mesh, "material");
        var areas = split.GroupAreasM2();
        Assert.Equal(new[] { "material: Ceiling", "material: Floor", "material: Wall 1", "material: Wall 2", "material: Wall 3", "material: Wall 4" },
            areas.Keys.OrderBy(k => k));
        Assert.Equal(12, areas["material: Floor"], 9);
        Assert.Equal(12, areas["material: Ceiling"], 9);
        // the walls numbered from the largest; each drawn in two halves is still one surface
        Assert.Equal(new[] { 10.0, 10.0, 7.5, 7.5 }, Enumerable.Range(1, 4).Select(i => Math.Round(areas[$"material: Wall {i}"], 9)));
        Assert.Equal(30, split.ToRoom(_ => Phase5Fixtures.Finish(0.1)).VolumeM3, 6);
        // listed in order, wherever their faces were drawn
        Assert.Equal(new[] { "material: Floor", "material: Ceiling", "material: Wall 1", "material: Wall 2", "material: Wall 3", "material: Wall 4" }, split.Groups);
    }

    [Fact]
    public void OtherGroups_AreLeftAsTheyWere_AndAScreensNameIsKept()
    {
        var b = Box("Room");
        b.AddQuad(P(1, 1, 0.5), P(2, 1, 0.5), P(2, 1, 1.5), P(1, 1, 1.5), "Screen");
        b.AddQuad(P(1, 2, 0.5), P(2, 2, 0.5), P(2, 2, 1.5), P(1, 2, 1.5), "Screen");
        var split = SurfaceSplit.Split(b.Build(), "Screen");

        Assert.Equal(new[] { "Room", "Screen: Wall 1", "Screen: Wall 2" }, split.Groups.OrderBy(g => g));
        Assert.All(split.Groups.Where(g => g != "Room"), g => Assert.True(SurfaceMesh.LooksLikeScreen(g)));
        Assert.Equal(20, split.Triangles.Count(t => t.Group == "Room"));
    }

    [Fact]
    public void SmallFacets_GoTogether_AndALevelSurfaceBetweenFloorAndCeilingIsNamedSo()
    {
        // a gallery at 1.2 m along one side, and a small block on the floor (faces under 0.5 m²), all in the room's group
        var b = Box();
        b.AddQuad(P(0, 2, 1.2), P(4, 2, 1.2), P(4, 3, 1.2), P(0, 3, 1.2), "material");
        b.AddQuad(P(1, 1, 0.3), P(1.4, 1, 0.3), P(1.4, 1.4, 0.3), P(1, 1.4, 0.3), "material");
        b.AddQuad(P(1, 1, 0), P(1.4, 1, 0), P(1.4, 1, 0.3), P(1, 1, 0.3), "material");
        var split = SurfaceSplit.Split(b.Build(), "material");
        var areas = split.GroupAreasM2();

        Assert.Equal(4, areas["material: Level"], 9);
        Assert.Equal(0.16 + 0.12, areas["material: Small surfaces"], 9);
        Assert.Equal(8, SurfaceSplit.Count(b.Build(), "material"));
    }

    [Fact]
    public void AGroupTheModelHasNot_IsAnError()
    {
        Assert.Throws<ArgumentException>(() => SurfaceSplit.Split(Box().Build(), "Walls"));
    }
}
