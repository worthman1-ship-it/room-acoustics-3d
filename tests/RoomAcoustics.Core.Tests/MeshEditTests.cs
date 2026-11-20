using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class MeshEditTests
{
    private static Vec3 P(double x, double y, double z) => new(x, y, z);

    /// <summary>A 4 x 3 x 2.5 m box: floor, ceiling and walls in groups of their own, the floor, the ceiling and the long walls each drawn in two halves.</summary>
    private static SurfaceMeshBuilder Box(bool ceiling = true)
    {
        var b = new SurfaceMeshBuilder();
        foreach (var (x0, x1) in new[] { (0.0, 2.0), (2.0, 4.0) })
        {
            b.AddQuad(P(x0, 0, 0), P(x1, 0, 0), P(x1, 3, 0), P(x0, 3, 0), "Floor");
            if (ceiling) b.AddQuad(P(x0, 0, 2.5), P(x1, 0, 2.5), P(x1, 3, 2.5), P(x0, 3, 2.5), "Ceiling");
            b.AddQuad(P(x0, 0, 0), P(x1, 0, 0), P(x1, 0, 2.5), P(x0, 0, 2.5), "Walls");
            b.AddQuad(P(x0, 3, 0), P(x1, 3, 0), P(x1, 3, 2.5), P(x0, 3, 2.5), "Walls");
        }
        b.AddQuad(P(0, 0, 0), P(0, 3, 0), P(0, 3, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        return b;
    }

    private static double Volume(SurfaceMesh mesh) => mesh.ToRoom(_ => Phase5Fixtures.Finish(0.1)).VolumeM3;

    [Fact]
    public void MovingACorner_TakesEveryFaceThatMeetsThere_WithIt()
    {
        var mesh = Box().Build();
        foreach (var (x, y) in new[] { (0.0, 0.0), (2.0, 0.0), (4.0, 0.0), (4.0, 3.0), (2.0, 3.0), (0.0, 3.0) })
            mesh = MeshEdit.MoveCorner(mesh, P(x, y, 2.5), P(x, y, 3.5));
        Assert.Equal(4 * 3 * 3.5, Volume(mesh), 6);
        Assert.Equal(12, mesh.GroupAreasM2()["Ceiling"], 6);
        Assert.Equal(2 * (4 + 3) * 3.5, mesh.GroupAreasM2()["Walls"], 6);
    }

    [Fact]
    public void MovingACorner_FindsItWithinAMillimetre_AndSaysSoWhenThereIsNone()
    {
        var moved = MeshEdit.MoveCorner(Box().Build(), P(4.0004, 3, 2.5), P(4, 3, 3));
        Assert.Contains(moved.Vertices, v => (v - P(4, 3, 3)).Length < 1e-9);
        var error = Assert.Throws<ArgumentException>(() => MeshEdit.MoveCorner(Box().Build(), P(2, 1.5, 2.5), P(2, 1.5, 3)));
        Assert.Contains("no corner", error.Message);
    }

    [Fact]
    public void MovingACornerOntoAnother_JoinsThem_AndDropsTheFacesThatCloseUp()
    {
        // the middle corner of the front wall's top edge, moved onto the next one along, leaves no face with no area behind
        var mesh = MeshEdit.MoveCorner(Box().Build(), P(2, 0, 2.5), P(4, 0, 2.5));
        Assert.DoesNotContain(mesh.Triangles, t => (mesh.Vertices[t.B] - mesh.Vertices[t.A]).Cross(mesh.Vertices[t.C] - mesh.Vertices[t.A]).Length < 1e-12);
        Assert.Equal(mesh.Vertices.Count, mesh.Vertices.Distinct().Count());
    }

    [Fact]
    public void DrawingAFace_ClosesABoxWithNoCeiling_InTheGroupGiven()
    {
        var open = Box(ceiling: false).Build();
        Assert.ThrowsAny<Exception>(() => Volume(open));
        var closed = MeshEdit.AddFace(open, new[] { P(0, 0, 2.5), P(2, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(2, 3, 2.5), P(0, 3, 2.5) }, "New ceiling");
        Assert.Equal(30, Volume(closed), 6);
        Assert.Equal(12, closed.GroupAreasM2()["New ceiling"], 9);
        // its corners are the walls' own, not new ones on top of them
        Assert.Equal(open.Vertices.Count, closed.Vertices.Count);
    }

    [Fact]
    public void DrawingAFace_CoversAConcaveOutline_Exactly()
    {
        var l = new[] { P(0, 0, 0), P(4, 0, 0), P(4, 1, 0), P(1, 1, 0), P(1, 3, 0), P(0, 3, 0) };
        var mesh = MeshEdit.AddFace(new SurfaceMesh(Array.Empty<Vec3>(), Array.Empty<MeshTriangle>()), l, "L");
        Assert.Equal(4 + 2, mesh.GroupAreasM2()["L"], 9);
    }

    [Theory]
    [InlineData("bent")]
    [InlineData("crossed")]
    [InlineData("two")]
    [InlineData("unnamed")]
    public void AFaceThatIsNotFlat_CrossesItself_HasTooFewCorners_OrNoGroup_IsRefused(string kind)
    {
        var corners = kind switch
        {
            "bent" => new[] { P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 3.0), P(0, 3, 2.5) },
            "crossed" => new[] { P(0, 0, 2.5), P(4, 3, 2.5), P(4, 0, 2.5), P(0, 3, 2.5) },
            "two" => new[] { P(0, 0, 2.5), P(4, 0, 2.5) },
            _ => new[] { P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(0, 3, 2.5) },
        };
        Assert.Throws<ArgumentException>(() => MeshEdit.AddFace(Box(ceiling: false).Build(), corners, kind == "unnamed" ? " " : "Ceiling"));
    }

    [Fact]
    public void DeletingASurface_TakesTheWholeFlatSurfaceTheFaceIsPartOf_InItsGroup()
    {
        var mesh = Box().Build();
        // one triangle of the front wall's first half: the wall's two halves go together, the other walls stay
        var t = mesh.Triangles.First(t => t.Group == "Walls" && new[] { t.A, t.B, t.C }.All(i => Math.Abs(mesh.Vertices[i].Y) < 1e-9 && mesh.Vertices[i].X <= 2));
        var without = MeshEdit.RemoveSurface(mesh, mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C]);
        Assert.Equal(2 * (4 + 3) * 2.5 - 4 * 2.5, without.GroupAreasM2()["Walls"], 9);
        Assert.Equal(12, without.GroupAreasM2()["Floor"], 9);
        Assert.Equal(without.Vertices.Count, without.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Count()); // no corner left unused
    }

    [Fact]
    public void DeletingAWallAndDrawingItAgain_LeavesTheRoomAsItWas()
    {
        var mesh = Box().Build();
        var t = mesh.Triangles.First(t => t.Group == "Walls" && new[] { t.A, t.B, t.C }.All(i => Math.Abs(mesh.Vertices[i].X - 4) < 1e-9));
        var open = MeshEdit.RemoveSurface(mesh, mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C]);
        Assert.ThrowsAny<Exception>(() => Volume(open));
        var again = MeshEdit.AddFace(open, new[] { P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5) }, "End wall");
        Assert.Equal(30, Volume(again), 6);
    }

    [Fact]
    public void DeletingAFaceThatIsNotThere_OrTheLastSurface_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => MeshEdit.RemoveSurface(Box().Build(), P(0, 0, 0), P(1, 1, 1), P(2, 2, 2)));
        var one = MeshEdit.AddFace(new SurfaceMesh(Array.Empty<Vec3>(), Array.Empty<MeshTriangle>()), new[] { P(0, 0, 0), P(1, 0, 0), P(1, 1, 0) }, "Only");
        Assert.Throws<ArgumentException>(() => MeshEdit.RemoveSurface(one, P(0, 0, 0), P(1, 0, 0), P(1, 1, 0)));
    }
}
