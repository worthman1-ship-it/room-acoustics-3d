using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class SectionLineTests
{
    private static SurfaceFinish Finish(double alpha = 0.2) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    [Fact]
    public void ALineKnowsItsLength_ItsDirection_AndWhichSideIsLeft()
    {
        var line = new SectionLine(1, 2, 4, 6);                     // 3 across, 4 up the plan: length 5

        Assert.Equal(5, line.LengthM, 12);
        Assert.Equal(0.6, line.Direction.X, 12);
        Assert.Equal(0.8, line.Direction.Y, 12);
        Assert.Equal(-0.8, line.Normal.X, 12);                      // to the left of the way it runs
        Assert.Equal(0.6, line.Normal.Y, 12);
        Assert.Equal(5, line.Across(new Vec3(4, 6, 1.7)), 12);
        Assert.Equal(0, line.Across(new Vec3(1, 2, 0)), 12);
        Assert.Equal(1, line.OffPlane(new Vec3(1 - 0.8, 2 + 0.6, 0)), 12);
        Assert.Equal(-2, line.OffPlane(new Vec3(1 + 1.6, 2 - 1.2, 3)), 12);
    }

    [Fact]
    public void ALineAlongX_GivesTheSameGridAsThePlaneOfConstantY_ButStartingAtTheLine()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        var line = RoomSection.Grid(room, new SectionLine(0, 2.5, 10, 2.5), 1.0, 0.5);
        var plane = RoomSection.Grid(room, SectionAxis.Y, 2.5, 1.0, 0.5);

        Assert.Equal(plane.Count, line.Count);
        for (var i = 0; i < plane.Count; i++)
        {
            Assert.Equal(plane[i].X, line[i].X, 9);
            Assert.Equal(plane[i].Y, line[i].Y, 9);
            Assert.Equal(plane[i].Z, line[i].Z, 9);
        }
    }

    [Fact]
    public void ADiagonalGrid_LiesOnThePlaneThroughTheLine_NoFartherThanItsLength_AndClearOfTheSurfaces()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());
        var line = new SectionLine(1, 1, 7, 9);                     // 10 m long, and it leaves the room's far wall

        var points = RoomSection.Grid(room, line, 1.0, 0.5);

        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Equal(0, line.OffPlane(p), 9));
        Assert.All(points, p => Assert.InRange(line.Across(p), 0.5 - 1e-9, 10));
        Assert.All(points, p => Assert.True(room.DistanceToBoundary(p) >= 0.5));
        Assert.Contains(points, p => p.X > 1.2 && p.Y > 1.2);       // really diagonal, not just along an axis
    }

    [Fact]
    public void ALineOutsideTheRoom_OfNoLength_OrWithAnEndThatIsNotANumber_IsRefused()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        Assert.Contains("No grid point", Assert.Throws<ArgumentException>(() => RoomSection.Grid(room, new SectionLine(20, 20, 30, 20), 1.0, 0.5)).Message);
        Assert.Contains("same point", Assert.Throws<ArgumentException>(() => RoomSection.Grid(room, new SectionLine(3, 3, 3, 3), 1.0, 0.5)).Message);
        Assert.Throws<ArgumentException>(() => RoomSection.Grid(room, new SectionLine(0, 0, double.NaN, 3), 1.0, 0.5));
        Assert.Throws<ArgumentException>(() => RoomSection.Outline(room, new SectionLine(3, 3, 3, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomSection.Grid(room, new SectionLine(0, 1, 5, 1), 0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomSection.Grid(room, new SectionLine(0, 1, 5, 1), 1, -1));
    }

    [Fact]
    public void TheOutlineAlongX_IsTheSameAsThePlaneOfConstantYs_AndAlongYIsTheSameAcrossFromTheStart()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());

        var line = RoomSection.Outline(room, new SectionLine(0, 2.5, 10, 2.5));
        var plane = RoomSection.Outline(room, SectionAxis.Y, 2.5);
        Assert.Equal(plane.Count, line.Count);
        Assert.Equal(plane.Select(Canonical).OrderBy(x => x), line.Select(Canonical).OrderBy(x => x));

        // up the plan at x = 4 from y = 0: the distance along the line is y, the same as for the plane x = 4
        var up = RoomSection.Outline(room, new SectionLine(4, 0, 4, 7));
        var x4 = RoomSection.Outline(room, SectionAxis.X, 4);
        Assert.Equal(x4.Select(Canonical).OrderBy(x => x), up.Select(Canonical).OrderBy(x => x));
    }

    /// <summary>A segment as text, its ends put in order and rounded, so that two lists of segments can be compared whatever the order or direction.</summary>
    private static string Canonical(SectionSegment s)
    {
        var a = (Math.Round(s.U1, 6), Math.Round(s.V1, 6));
        var b = (Math.Round(s.U2, 6), Math.Round(s.V2, 6));
        return a.CompareTo(b) <= 0 ? $"{a}-{b}" : $"{b}-{a}";
    }

    [Fact]
    public void ADiagonalCutThroughABox_HasTheFloorAndCeilingAsLongAsTheCutAndTheWallsAsTall()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish());
        // from the middle of the x = 0 wall to the far wall at y = 6: 11.18 m long, and the room is cut right across
        var diagonal = new SectionLine(0, 1, 10, 6);
        var outline = RoomSection.Outline(room, diagonal);

        var floor = outline.Where(s => s.V1 == 0 && s.V2 == 0).Sum(s => Math.Abs(s.U2 - s.U1));
        var ceiling = outline.Where(s => s.V1 == 3 && s.V2 == 3).Sum(s => Math.Abs(s.U2 - s.U1));
        Assert.Equal(diagonal.LengthM, floor, 9);
        Assert.Equal(diagonal.LengthM, ceiling, 9);

        // the end walls are the two vertical pieces, at the start and end of the line, 3 m tall
        var uprights = outline.Where(s => Math.Abs(s.U1 - s.U2) < 1e-9).ToList();
        Assert.Equal(2, uprights.Count);
        Assert.Equal(0, uprights.Min(s => s.U1), 9);
        Assert.Equal(diagonal.LengthM, uprights.Max(s => s.U1), 9);
        Assert.All(uprights, s => Assert.Equal(3, Math.Abs(s.V2 - s.V1), 9));
    }

    [Fact]
    public void ALineAcrossAnLRoom_ShowsOnlyThePartOfThePlaneInsideTheRoom()
    {
        var room = Phase5Fixtures.LRoom(Finish(), 3);               // x 0 to 4 and y 0 to 1, plus x 0 to 1 and y 0 to 4
        var outline = RoomSection.Outline(room, new SectionLine(2, -1, 2, 5));   // up the plan at x = 2: only the long arm is cut

        var floor = outline.Where(s => s.V1 == 0 && s.V2 == 0).Sum(s => Math.Abs(s.U2 - s.U1));
        Assert.Equal(1, floor, 9);                                  // y from 0 to 1, one metre of floor
    }
}
