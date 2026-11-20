using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The list of specular reflections at a receiver: which surfaces, how late, how loud against the direct sound.</summary>
public sealed class ReflectionListTests
{
    private static SurfaceFinish Finish(double alpha, double scatter = 0) =>
        new(new AbsorptionMaterial("flat", "test", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scatter));

    private static readonly Vec3 Source = new(2, 4, 1.6);
    private static readonly Vec3 Receiver = new(9, 4, 1.2);

    [Fact]
    public void FirstOrder_OfABox_HasOneReflectionFromEachSurface_InArrivalOrder()
    {
        var room = Room3D.Shoebox(12, 8, 4, Finish(0.2));
        var list = ReflectionList.For(room, Source, Receiver, 1, 20, 60);
        Assert.Equal(6, list.Count);
        Assert.All(list, r => Assert.Equal(1, r.Order));
        Assert.Equal(list.OrderBy(r => r.DistanceM).Select(r => r.DistanceM), list.Select(r => r.DistanceM));
        Assert.Equal(new[] { "Ceiling", "Floor", "Wall 1", "Wall 2", "Wall 3", "Wall 4" }, list.Select(r => r.FaceNames[0]).Order().ToArray());
        Assert.All(list, r => Assert.True(r.DelayMs > 0));
    }

    [Fact]
    public void TheFloorReflection_HasTheImageSourcesDelayAndTheSpreadingAbsorptionAndAirLoss()
    {
        var room = Room3D.Shoebox(12, 8, 4, Finish(0.2));
        var floor = ReflectionList.For(room, Source, Receiver, 1, 20, 60).Single(r => r.FaceNames[0] == "Floor");
        var direct = (Receiver - Source).Length;
        var image = new Vec3(Source.X, Source.Y, -Source.Z);
        var length = (Receiver - image).Length;
        Assert.Equal(length, floor.DistanceM, 9);
        Assert.Equal((length - direct) / EnergyRayTracer.SpeedOfSound(20) * 1000, floor.DelayMs, 6);
        var air = AirAbsorption.AttenuationDbPerMetre(1000, AirAbsorption.ReferencePressurePa, 20, 60);
        var expected = 20 * Math.Log10(direct / length) + 10 * Math.Log10(0.8) - air * (length - direct);
        Assert.Equal(expected, floor.LevelDb[4], 1); // the 1 kHz band; the receiver sphere makes the spreading very slightly inexact
        Assert.Equal(floor.Path.Count, 3);
        Assert.Equal(0, floor.Path[1].Z, 9); // the reflection point is on the floor
    }

    [Fact]
    public void ScatteringTakesSomeOfTheEnergyOutOfASpecularPath_AndAnAlphaOfOneRemovesIt()
    {
        var plain = ReflectionList.For(Room3D.Shoebox(12, 8, 4, Finish(0.2)), Source, Receiver, 1, 20, 60).Single(r => r.FaceNames[0] == "Floor");
        var rough = ReflectionList.For(Room3D.Shoebox(12, 8, 4, Finish(0.2, 0.5)), Source, Receiver, 1, 20, 60).Single(r => r.FaceNames[0] == "Floor");
        Assert.Equal(10 * Math.Log10(0.5), rough.LevelDb[4] - plain.LevelDb[4], 6);
        var dead = ReflectionList.For(Room3D.Shoebox(12, 8, 4, Finish(1.0)), Source, Receiver, 1, 20, 60);
        Assert.Empty(dead); // nothing is reflected, so there is nothing to list
    }

    [Fact]
    public void HigherOrders_AddPathsThroughMoreSurfaces_AndOrderZeroIsEmpty()
    {
        var room = Room3D.Shoebox(12, 8, 4, Finish(0.2));
        var two = ReflectionList.For(room, Source, Receiver, 2, 20, 60);
        Assert.True(two.Count > 6);
        Assert.Contains(two, r => r.Order == 2 && r.FaceNames.Count == 2 && r.Path.Count == 4);
        Assert.Empty(ReflectionList.For(room, Source, Receiver, 0, 20, 60));
    }
}
