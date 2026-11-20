using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class AudienceEdgeTests
{
    /// <summary>
    /// A 10 x 8 m room 4 m high whose floor is a 6 x 8 m seating block against the back (x = 10) and side walls, and a 4 m wide strip in front of it at
    /// the same level (x = 0 to 4), each quad sharing its corners, so the seating's front edge (8 m long, at x = 4) is beside the strip.
    /// </summary>
    private static SurfaceMesh Room(double frontStepM = 0)
    {
        var b = new SurfaceMeshBuilder();
        Vec3 P(double x, double y, double z) => new(x, y, z);
        var f = frontStepM; // the strip in front stands this much lower than the seating (a step down to the stage or the stalls)
        b.AddQuad(P(4, 0, 0), P(10, 0, 0), P(10, 8, 0), P(4, 8, 0), "Seating");
        b.AddQuad(P(0, 0, -f), P(4, 0, -f), P(4, 8, -f), P(0, 8, -f), "Front");
        if (f > 0) b.AddQuad(P(4, 0, -f), P(4, 8, -f), P(4, 8, 0), P(4, 0, 0), "Step");
        b.AddQuad(P(10, 0, 0), P(10, 0, 4), P(10, 8, 4), P(10, 8, 0), "Walls");          // back
        b.AddQuad(P(0, 0, -f), P(0, 8, -f), P(0, 8, 4), P(0, 0, 4), "Walls");            // front wall
        b.AddQuad(P(4, 0, 0), P(4, 0, 4), P(10, 0, 4), P(10, 0, 0), "Walls");            // side by the seating
        b.AddQuad(P(4, 8, 0), P(10, 8, 0), P(10, 8, 4), P(4, 8, 4), "Walls");
        b.AddQuad(P(0, 0, -f), P(0, 0, 4), P(4, 0, 4), P(4, 0, -f), "Walls");            // side by the strip
        b.AddQuad(P(0, 8, -f), P(4, 8, -f), P(4, 8, 4), P(0, 8, 4), "Walls");
        return b.Build();
    }

    [Fact]
    public void ABlockAgainstThreeWalls_IsExposedAlongItsFrontOnly()
    {
        Assert.Equal(8, AudienceEdge.ExposedLengthM(Room(), "Seating"), 9);
    }

    [Fact]
    public void ABlockAboveAStepDown_IsExposedAlongTheStep()
    {
        Assert.Equal(8, AudienceEdge.ExposedLengthM(Room(0.6), "Seating"), 9);    // the step's face goes down from the edge, not up
    }

    [Fact]
    public void TheStripBelowAHighStepUp_IsShieldedByIt_ButNotByALowOne()
    {
        Assert.Equal(0, AudienceEdge.ExposedLengthM(Room(0.6), "Front"), 9);      // its back edge is under a 0.6 m step: shielded; its other edges are walls
        Assert.Equal(8, AudienceEdge.ExposedLengthM(Room(0.2), "Front"), 9);      // a 0.2 m step is no shield
    }

    [Fact]
    public void AFloorAgainstWallsAllRound_HasNoExposedEdge_AndAGroupThatIsNotThereHasNone()
    {
        var b = new SurfaceMeshBuilder();
        b.AddQuad(new(0, 0, 0), new(5, 0, 0), new(5, 4, 0), new(0, 4, 0), "Floor");
        b.AddQuad(new(0, 0, 0), new(0, 0, 3), new(5, 0, 3), new(5, 0, 0), "Walls");
        b.AddQuad(new(5, 0, 0), new(5, 0, 3), new(5, 4, 3), new(5, 4, 0), "Walls");
        b.AddQuad(new(5, 4, 0), new(5, 4, 3), new(0, 4, 3), new(0, 4, 0), "Walls");
        b.AddQuad(new(0, 4, 0), new(0, 4, 3), new(0, 0, 3), new(0, 0, 0), "Walls");
        var mesh = b.Build();
        Assert.Equal(0, AudienceEdge.ExposedLengthM(mesh, "Floor"), 9);
        Assert.Equal(0, AudienceEdge.ExposedLengthM(mesh, "Nothing"), 9);
    }

    [Fact]
    public void AnEdgeWithNothingBesideIt_IsExposed()
    {
        var b = new SurfaceMeshBuilder();
        b.AddQuad(new(0, 0, 0), new(3, 0, 0), new(3, 2, 0), new(0, 2, 0), "Seating");
        Assert.Equal(10, AudienceEdge.ExposedLengthM(b.Build(), "Seating"), 9);
    }
}
