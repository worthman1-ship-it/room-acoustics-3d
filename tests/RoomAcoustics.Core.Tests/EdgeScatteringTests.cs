using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class EdgeScatteringTests
{
    private const double C = 343;

    [Fact]
    public void BelowTheLimitFrequency_TheSpecularShareFallsAsFOverFg()
    {
        // a 2 m surface met head on, 4 m from the source and 4 m on: a* = 4, f_g = 343 × 4 / (2 × 4) = 171.5 Hz
        Assert.Equal(1 - 125 / 171.5, EdgeScattering.Coefficient(2, 1, 4, 4, 125, C), 9);
        Assert.Equal(0.5, EdgeScattering.Coefficient(2, 1, 4, 4, 171.5 / 2, C), 9);
        Assert.Equal(0, EdgeScattering.Coefficient(2, 1, 4, 4, 250, C));
    }

    [Fact]
    public void SmallSurfaces_LongPaths_AndGrazingSound_AreScatteredMost()
    {
        var reference = EdgeScattering.Coefficient(2, 1, 4, 4, 125, C);
        Assert.True(EdgeScattering.Coefficient(1, 1, 4, 4, 125, C) > reference);
        Assert.True(EdgeScattering.Coefficient(2, 1, 10, 10, 125, C) > reference);
        Assert.True(EdgeScattering.Coefficient(2, 0.5, 4, 4, 125, C) > reference);
        Assert.Equal(1, EdgeScattering.Coefficient(2, 0, 4, 4, 125, C));
        // a large wall met head on is not scattered
        Assert.Equal(0, EdgeScattering.Coefficient(10, 1, 4, 4, 63, C));
        Assert.Equal(0, EdgeScattering.Coefficient(0, 1, 4, 4, 63, C));
    }

    [Fact]
    public void TheSurfacesOwnScattering_IsAddedTo()
    {
        var room = Room3D.Shoebox(2, 2, 2, Phase5Fixtures.Finish(0.1));
        var edges = new EdgeScattering(room, 125, C);
        var diffraction = EdgeScattering.Coefficient(room.SurfaceSizesM[0], 1, 4, 4, 125, C);

        Assert.Equal(1 - (1 - 0.2) * (1 - diffraction), edges.Combined(0, 0.2, 1, 4, 4), 9);
    }

    [Fact]
    public void ASurfacesSize_IsFourTimesItsAreaOverItsPerimeter_WhateverTrianglesItIsDrawnIn()
    {
        var box = Room3D.Shoebox(4, 3, 2.5, Phase5Fixtures.Finish(0.1));
        Assert.Contains(Math.Round(4 * 12 / 14.0, 9), box.SurfaceSizesM.Select(s => Math.Round(s, 9)));
        Assert.Contains(Math.Round(4 * 10 / 13.0, 9), box.SurfaceSizesM.Select(s => Math.Round(s, 9)));

        // a 4 x 3 x 2.5 m box drawn in halves either side of x = 2 (so every edge is shared), with the front wall's right half of another finish:
        // the floor's halves are one surface, as are the back wall's; the front wall is two surfaces
        var b = new SurfaceMeshBuilder();
        Vec3 P(double x, double y, double z) => new(x, y, z);
        foreach (var (x0, x1) in new[] { (0.0, 2.0), (2.0, 4.0) })
        {
            b.AddQuad(P(x0, 0, 0), P(x1, 0, 0), P(x1, 3, 0), P(x0, 3, 0), "Floor");
            b.AddQuad(P(x0, 0, 2.5), P(x1, 0, 2.5), P(x1, 3, 2.5), P(x0, 3, 2.5), "Ceiling");
            b.AddQuad(P(x0, 3, 0), P(x1, 3, 0), P(x1, 3, 2.5), P(x0, 3, 2.5), "Walls");
            b.AddQuad(P(x0, 0, 0), P(x1, 0, 0), P(x1, 0, 2.5), P(x0, 0, 2.5), x0 == 0 ? "Walls" : "Glass");
        }
        b.AddQuad(P(0, 0, 0), P(0, 3, 0), P(0, 3, 2.5), P(0, 0, 2.5), "Walls");
        b.AddQuad(P(4, 0, 0), P(4, 3, 0), P(4, 3, 2.5), P(4, 0, 2.5), "Walls");
        var finishes = new Dictionary<string, SurfaceFinish> { ["Floor"] = Phase5Fixtures.Finish(0.1), ["Ceiling"] = Phase5Fixtures.Finish(0.1),
            ["Walls"] = Phase5Fixtures.Finish(0.1), ["Glass"] = Phase5Fixtures.Finish(0.2) };
        var room = b.Build().ToRoom(g => finishes[g]);

        // each face's size, by the plane it lies in and its group
        IEnumerable<double> Sizes(string group, Func<RoomFace, bool> where) =>
            room.Faces.Select((f, i) => (f, i)).Where(x => x.f.Name.StartsWith(group + " ") && where(x.f)).Select(x => Math.Round(room.SurfaceSizesM[x.i], 9)).Distinct();
        Assert.Equal(new[] { Math.Round(4 * 12 / 14.0, 9) }, Sizes("Floor", _ => true));
        Assert.Equal(new[] { Math.Round(4 * 10 / 13.0, 9) }, Sizes("Walls", f => f.Vertices.All(v => v.Y == 3)));
        Assert.Equal(new[] { Math.Round(4 * 5 / 9.0, 9) }, Sizes("Walls", f => f.Vertices.All(v => v.Y == 0)));
        Assert.Equal(new[] { Math.Round(4 * 5 / 9.0, 9) }, Sizes("Glass", _ => true));
        Assert.Equal(new[] { Math.Round(4 * 7.5 / 11.0, 9) }, Sizes("Walls", f => f.Vertices.All(v => v.X == 0)));
    }
}
