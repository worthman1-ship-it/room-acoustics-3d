using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>A room of thousands of faces must be recognised as convex or not without testing every corner against every face.</summary>
public class LargeRoomTests
{
    private static SurfaceFinish Finish() =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)0.1, 8).ToArray()), OctaveSpectrum.Flat(0.2));

    /// <summary>A UV sphere of triangles; <paramref name="dent"/> pulls one ring of vertices inwards.</summary>
    private static Room3D Sphere(int rings, int segments, double dent = 0)
    {
        var vertices = new List<Vec3> { new(0, 0, -10) };
        for (var i = 1; i < rings; i++)
        {
            var theta = Math.PI * i / rings;
            var r = i == rings / 2 ? 10 - dent : 10;
            for (var j = 0; j < segments; j++)
            {
                var phi = 2 * Math.PI * j / segments;
                vertices.Add(new Vec3(r * Math.Sin(theta) * Math.Cos(phi), r * Math.Sin(theta) * Math.Sin(phi), -r * Math.Cos(theta)));
            }
        }
        vertices.Add(new Vec3(0, 0, 10));
        int At(int i, int j) => 1 + (i - 1) * segments + ((j % segments) + segments) % segments;
        var finish = Finish();
        var faces = new List<PlanarFace>();
        void Add(int a, int b, int c) => faces.Add(new PlanarFace("Dome", new[] { vertices[a], vertices[b], vertices[c] }, finish));
        for (var j = 0; j < segments; j++) Add(0, At(1, j + 1), At(1, j));
        for (var i = 1; i < rings - 1; i++)
            for (var j = 0; j < segments; j++)
            {
                Add(At(i, j), At(i, j + 1), At(i + 1, j + 1));
                Add(At(i, j), At(i + 1, j + 1), At(i + 1, j));
            }
        for (var j = 0; j < segments; j++) Add(vertices.Count - 1, At(rings - 1, j), At(rings - 1, j + 1));
        return new Room3D(faces);
    }

    [Fact]
    public void ABigConvexDome_IsConvex()
    {
        var room = Sphere(60, 60); // about 7 000 faces and 3 500 corners: past the size where every corner is tested against every face
        Assert.True(room.Faces.Count * 3L * room.Faces.Count > 20_000_000);
        Assert.True(room.IsConvex);
    }

    [Fact]
    public void ABigDomeWithADentedRing_IsNotConvex()
    {
        var room = Sphere(60, 60, dent: 1.5);
        Assert.False(room.IsConvex);
    }

    [Fact]
    public void TheDistanceToAnL_ShapedRoomsSurface_IsTheNearestOfItsWallsFloorAndCeiling()
    {
        var footprint = new (double X, double Y)[] { (0, 0), (10, 0), (10, 4), (4, 4), (4, 9), (0, 9) };
        var room = Room3D.Extrude(footprint, 3, Finish(), Finish(), Finish());
        Assert.False(room.IsConvex);
        double ToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
        {
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            return Math.Sqrt(Math.Pow(p.X - (a.X + t * dx), 2) + Math.Pow(p.Y - (a.Y + t * dy), 2));
        }
        var rng = new Random(5);
        var checkedPoints = 0;
        while (checkedPoints < 200)
        {
            var p = new Vec3(rng.NextDouble() * 10, rng.NextDouble() * 9, rng.NextDouble() * 3);
            if (!room.Contains(p)) continue;
            var expected = Math.Min(p.Z, 3 - p.Z);
            for (var i = 0; i < footprint.Length; i++)
                expected = Math.Min(expected, ToSegment((p.X, p.Y), footprint[i], footprint[(i + 1) % footprint.Length]));
            Assert.Equal(expected, room.DistanceToBoundary(p), 9);
            checkedPoints++;
        }
    }

    [Fact]
    public void ASmallDome_IsDecidedTheSameWay()
    {
        Assert.True(Sphere(12, 12).IsConvex);
        Assert.False(Sphere(12, 12, dent: 1.5).IsConvex);
    }
}
