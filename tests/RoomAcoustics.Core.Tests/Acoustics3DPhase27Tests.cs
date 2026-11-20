using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class VectorScatteringTests
{
    private static SurfaceFinish Finish(double alpha, double scattering) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scattering));

    private static readonly Vec3 Source = new(2, 2, 1.5);

    /// <summary>The first two legs of each ray, as unit directions, for the rays that have two.</summary>
    private static List<(Vec3 First, Vec3 Second)> FirstTurns(double scattering, ScatteringModel model)
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.1, scattering));
        var rays = BilliardRays.Trace(room, Source, 4000, 2, 4, new RayTraceOptions(ScatteringModel: model));
        return rays.Where(r => r.Points.Count >= 3)
            .Select(r => ((r.Points[1] - r.Points[0]).Normalized(), (r.Points[2] - r.Points[1]).Normalized()))
            .ToList();
    }

    /// <summary>A reflection in a wall of a box flips one component and keeps the size of the others; any other turn is not specular.</summary>
    private static bool IsSpecular(Vec3 first, Vec3 second) =>
        Math.Abs(Math.Abs(first.X) - Math.Abs(second.X)) < 1e-9
        && Math.Abs(Math.Abs(first.Y) - Math.Abs(second.Y)) < 1e-9
        && Math.Abs(Math.Abs(first.Z) - Math.Abs(second.Z)) < 1e-9;

    [Fact]
    public void AllScatteringIsTheSameInBothModels_AndNoScatteringToo()
    {
        foreach (var s in new[] { 0.0, 1.0 })
        {
            var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, s));
            var receivers = new[] { new Vec3(6, 4, 1.2), new Vec3(8, 5, 1.2) };

            // the surfaces' own scattering only: the edges' diffraction would make it neither 0 nor 1
            var probabilistic = EnergyRayTracer.TraceReceivers(room, Source, receivers, new RayTraceOptions(RayCount: 20000, EdgeDiffraction: false));
            var vector = EnergyRayTracer.TraceReceivers(room, Source, receivers, new RayTraceOptions(RayCount: 20000, ScatteringModel: ScatteringModel.VectorBased, EdgeDiffraction: false));

            for (var r = 0; r < receivers.Length; r++)
                for (var b = 0; b < 8; b++)
                    Assert.Equal(probabilistic[r].Energy[b], vector[r].Energy[b]);
        }
    }

    [Fact]
    public void WithPartialScattering_TheProbabilisticModelKeepsSomeRaysSpecular_AndTheVectorModelNone()
    {
        var probabilistic = FirstTurns(0.5, ScatteringModel.Probabilistic);
        var vector = FirstTurns(0.5, ScatteringModel.VectorBased);

        var share = probabilistic.Count(t => IsSpecular(t.First, t.Second)) / (double)probabilistic.Count;
        Assert.InRange(share, 0.45, 0.55);                                      // half of them were not scattered at all
        Assert.Equal(0, vector.Count(t => IsSpecular(t.First, t.Second)));      // every one is turned a little
    }

    [Fact]
    public void ABlendedRayLeavesTheWall_BetweenTheSpecularDirectionAndAnyOther()
    {
        // on average a vector-scattered ray keeps more of the specular direction than a fully diffuse one: its direction along the
        // wall's normal (the one component a reflection flips) stays near the specular value
        double MeanAlongNormalError(double s, ScatteringModel model)
        {
            var turns = FirstTurns(s, model);
            return turns.Average(t => Math.Min(
                Math.Abs(t.Second.X - SpecularOf(t.First).X) + Math.Abs(t.Second.Y - SpecularOf(t.First).Y) + Math.Abs(t.Second.Z - SpecularOf(t.First).Z),
                double.MaxValue));
        }

        var low = MeanAlongNormalError(0.2, ScatteringModel.VectorBased);
        var middle = MeanAlongNormalError(0.5, ScatteringModel.VectorBased);
        var high = MeanAlongNormalError(1.0, ScatteringModel.VectorBased);
        Assert.True(low < middle && middle < high, $"the turn should grow with s: {low:0.00}, {middle:0.00}, {high:0.00}");
    }

    /// <summary>The direction after the wall the first leg hit, for the box: the component along that wall's normal flips. The wall is the one whose coordinate the leg ended on.</summary>
    private static Vec3 SpecularOf(Vec3 first)
    {
        // the box is 10 x 7 x 3 with the source inside; the first leg ends on the wall it travels toward fastest in relative terms
        var tX = first.X > 0 ? (10 - Source.X) / first.X : first.X < 0 ? -Source.X / first.X : double.MaxValue;
        var tY = first.Y > 0 ? (7 - Source.Y) / first.Y : first.Y < 0 ? -Source.Y / first.Y : double.MaxValue;
        var tZ = first.Z > 0 ? (3 - Source.Z) / first.Z : first.Z < 0 ? -Source.Z / first.Z : double.MaxValue;
        var t = Math.Min(tX, Math.Min(tY, tZ));
        return t == tX ? new Vec3(-first.X, first.Y, first.Z) : t == tY ? new Vec3(first.X, -first.Y, first.Z) : new Vec3(first.X, first.Y, -first.Z);
    }

    [Fact]
    public void TheVectorModelNeverTurnsARayIntoTheWall()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.1, 0.7));
        var rays = BilliardRays.Trace(room, Source, 3000, 6, 4, new RayTraceOptions(ScatteringModel: ScatteringModel.VectorBased));

        // every point of every path is inside the room: no ray left through a wall
        foreach (var ray in rays)
            foreach (var p in ray.Points)
                Assert.True(p.X >= -1e-6 && p.X <= 10 + 1e-6 && p.Y >= -1e-6 && p.Y <= 7 + 1e-6 && p.Z >= -1e-6 && p.Z <= 3 + 1e-6);
        Assert.Contains(rays, r => r.Scattered);
    }

    [Fact]
    public void TheVectorModelRunsAsPureRayTracing_WhateverTheImageSourceOrder()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, 0.4));
        var receivers = new[] { new Vec3(6, 4, 1.2) };

        var asked = EnergyRayTracer.TraceReceivers(room, Source, receivers, new RayTraceOptions(RayCount: 20000, ImageSourceOrder: 3, ScatteringModel: ScatteringModel.VectorBased));
        var pure = EnergyRayTracer.TraceReceivers(room, Source, receivers, new RayTraceOptions(RayCount: 20000, ImageSourceOrder: 0, ScatteringModel: ScatteringModel.VectorBased));
        var hybrid = EnergyRayTracer.TraceReceivers(room, Source, receivers, new RayTraceOptions(RayCount: 20000, ImageSourceOrder: 3));

        for (var b = 0; b < 8; b++) Assert.Equal(pure[0].Energy[b], asked[0].Energy[b]);
        Assert.NotEqual(hybrid[0].Energy[4], asked[0].Energy[4]);
    }

    [Fact]
    public void TheModelsDifferInTheEnergyAReceiverGets_ButNotByMuch()
    {
        // neither model creates or destroys energy, but the vector blend turns rays toward the surface normal and so crowds them toward the
        // middle of the room, where a receiver gets more (measured here: 16 % more in this room at s = 0.6; the README gives the sweep).
        // The bound is there to catch a model that gains or loses energy outright, and the lower one to show that they are not the same.
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, 0.6));
        var receivers = new[] { new Vec3(6, 4, 1.2) };

        var probabilistic = EnergyRayTracer.TraceReceivers(room, Source, receivers, new RayTraceOptions(RayCount: 150000));
        var vector = EnergyRayTracer.TraceReceivers(room, Source, receivers, new RayTraceOptions(RayCount: 150000, ScatteringModel: ScatteringModel.VectorBased));

        var a = probabilistic[0].Energy[4].Sum();
        var b = vector[0].Energy[4].Sum();
        Assert.InRange(b / a, 1.02, 1.25);
    }
}
