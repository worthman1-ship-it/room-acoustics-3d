using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class MeshCheckTests
{
    private static Vec3 P(double x, double y, double z) => new(x, y, z);

    /// <summary>A 4 x 3 x 2.5 m box, with the faces left out that <paramref name="skip"/> names, and the ceiling's corner at (4, 3) raised by <paramref name="lift"/>.</summary>
    private static SurfaceMeshBuilder Box(string? skip = null, double lift = 0, bool splitFloor = false)
    {
        var b = new SurfaceMeshBuilder();
        if (splitFloor)
        {
            b.AddQuad(P(0, 0, 0), P(2, 0, 0), P(2, 3, 0), P(0, 3, 0), "Floor");
            b.AddQuad(P(2, 0, 0), P(4, 0, 0), P(4, 3, 0), P(2, 3, 0), "Floor");
        }
        else b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 3, 0), P(0, 3, 0), "Floor");
        if (skip != "Ceiling") b.AddQuad(P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5 + lift), P(0, 3, 2.5), "Ceiling");
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 0, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        b.AddQuad(P(4, 3, 0), P(0, 3, 0), P(0, 3, 2.5), P(4, 3, 2.5), "Walls");
        b.AddQuad(P(0, 3, 0), P(0, 0, 0), P(0, 0, 2.5), P(0, 3, 2.5), "Walls");
        return b;
    }

    [Fact]
    public void ASliverAlongAWallsTopEdge_IsFoldedIn_SoTheRoomIsClosed()
    {
        // as SketchUp exports a wall that meets a ceiling drawn in two pieces: the wall's top edge joined to the ceiling's by a sliver with no area, three
        // corners in a line, which only the sliver's short edges share with the ceiling
        var b = new SurfaceMeshBuilder();
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 3, 0), P(0, 3, 0), "Floor");
        b.AddQuad(P(0, 0, 2.5), P(2, 0, 2.5), P(2, 3, 2.5), P(0, 3, 2.5), "Ceiling");
        b.AddQuad(P(2, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(2, 3, 2.5), "Ceiling");
        b.AddQuad(P(0, 0, 0), P(4, 0, 0), P(4, 0, 2.5), P(0, 0, 2.5), "Walls");
        b.AddTriangle(P(4, 0, 2.5), P(2, 0, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        b.AddQuad(P(4, 3, 0), P(0, 3, 0), P(0, 3, 2.5), P(2, 3, 2.5), "Walls");
        b.AddTriangle(P(4, 3, 0), P(2, 3, 2.5), P(4, 3, 2.5), "Walls");
        b.AddQuad(P(0, 3, 0), P(0, 0, 0), P(0, 0, 2.5), P(0, 3, 2.5), "Walls");
        var mesh = b.Build();

        Assert.Empty(MeshCheck.Check(mesh).Faults);
        var room = mesh.ToRoom(_ => Phase5Fixtures.Finish(0.1));
        Assert.Equal(30, room.VolumeM3, 9);
        Assert.Equal(2 * 12 + 2 * 4 * 2.5 + 2 * 3 * 2.5, room.TotalAreaM2, 9);
    }

    [Fact]
    public void AClosedBox_HasNoFaults()
    {
        var check = MeshCheck.Check(Box().Build());
        Assert.Equal(0, check.OpenEdges);
        Assert.Equal(0, check.CrowdedEdges);
        Assert.Equal(1, check.Parts);
        Assert.Empty(check.Faults);
    }

    [Fact]
    public void AMissingCeiling_IsOneHoleRoundTheTopOfTheWalls()
    {
        var check = MeshCheck.Check(Box(skip: "Ceiling").Build());
        var hole = Assert.Single(check.Faults);
        Assert.Equal(MeshFaultKind.Hole, hole.Kind);
        Assert.Equal(4, check.OpenEdges);
        Assert.Equal(14, hole.LengthM, 9);
        Assert.All(hole.Edges, e => { Assert.Equal(2.5, e.A.Z, 9); Assert.Equal(2.5, e.B.Z, 9); });
        Assert.Equal(new[] { "Walls" }, hole.Groups);
        Assert.Contains("missing", hole.Hint);
    }

    [Fact]
    public void AFloorSplitWhereTheWallsAreNot_IsFoundAsTJunctions()
    {
        var check = MeshCheck.Check(Box(splitFloor: true).Build());
        Assert.Equal(2, check.Faults.Count);
        Assert.All(check.Faults, f =>
        {
            Assert.Equal(MeshFaultKind.Hole, f.Kind);
            Assert.Contains("T-junction", f.Hint);
            Assert.Contains("x 2.00", f.Hint);
            Assert.Equal(8, f.LengthM, 9); // the long wall edge and the two halves of the floor's
        });
    }

    [Fact]
    public void CornersThatNearlyMeet_AreFoundAsAGap()
    {
        var check = MeshCheck.Check(Box(lift: 0.01).Build());
        var hole = Assert.Single(check.Faults);
        Assert.Contains("10 mm apart", hole.Hint);
        Assert.Contains("x 4.00, y 3.00", hole.Hint);
        Assert.Throws<ArgumentException>(() => Box(lift: 0.01).Build().ToRoom(_ => new SurfaceFinish(AbsorptionMaterial.Presets.HardReflective)));
    }

    [Fact]
    public void AFaceDrawnTwice_IsCrowded()
    {
        var b = Box();
        b.AddQuad(P(0, 0, 2.5), P(4, 0, 2.5), P(4, 3, 2.5), P(0, 3, 2.5), "Ceiling tiles");
        var check = MeshCheck.Check(b.Build());
        var crowded = Assert.Single(check.Faults);
        Assert.Equal(MeshFaultKind.Crowded, crowded.Kind);
        Assert.Equal(5, check.CrowdedEdges); // the four round the ceiling and its diagonal
        Assert.Contains("Ceiling tiles", crowded.Groups);
        Assert.Contains("Up to 4 faces", crowded.Hint);
    }

    [Fact]
    public void ASolidInsideTheRoom_IsASeparatePartBoxedWhereItIs()
    {
        var b = Box();
        // a closed 1 m cube standing on the floor
        Vec3 C(int i) => P(1 + (i & 1), 1 + ((i >> 1) & 1), (i >> 2) & 1);
        foreach (var (a, bb, c, d) in new[] { (0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3) })
            b.AddQuad(C(a), C(bb), C(c), C(d), "Desk");
        var check = MeshCheck.Check(b.Build());
        Assert.Equal(2, check.Parts);
        var part = Assert.Single(check.Faults);
        Assert.Equal(MeshFaultKind.Separate, part.Kind);
        Assert.Equal(new[] { "Desk" }, part.Groups);
        Assert.Equal(12, part.Edges.Count);
        Assert.Equal(12, part.LengthM, 9);
        Assert.Contains("12 faces", part.Hint);
    }

    [Fact]
    public void TheRoomIsThePartWithTheMostArea_NotTheMostFaces()
    {
        // an open room of 10 triangles, and a desk cube of 12 inside it
        var b = Box(skip: "Ceiling");
        Vec3 C(int i) => P(1 + (i & 1), 1 + ((i >> 1) & 1), (i >> 2) & 1);
        foreach (var (a, bb, c, d) in new[] { (0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3) })
            b.AddQuad(C(a), C(bb), C(c), C(d), "Desk");
        var part = Assert.Single(MeshCheck.Check(b.Build()).Faults, f => f.Kind == MeshFaultKind.Separate);
        Assert.Equal(new[] { "Desk" }, part.Groups);
    }

    [Fact]
    public void ScreenGroups_AreLeftOut()
    {
        var b = Box();
        b.AddQuad(P(2, 0.5, 0), P(2, 2.5, 0), P(2, 2.5, 1.5), P(2, 0.5, 1.5), "Screens");
        Assert.NotEmpty(MeshCheck.Check(b.Build()).Faults);
        Assert.Empty(MeshCheck.Check(b.Build(), SurfaceMesh.LooksLikeScreen).Faults);
    }
}
