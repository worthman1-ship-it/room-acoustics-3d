using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class DelayedSourceTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0.2));

    private static readonly Room3D Room = Room3D.Shoebox(10, 7, 3, Finish(0.25));
    private static readonly Vec3 Position = new(2, 2, 1.5);
    private static readonly Vec3[] Receivers = { new(7, 4, 1.2), new(8, 5, 1.0) };
    private static readonly RayTraceOptions Options = new(RayCount: 20000, ImageSourceOrder: 2);

    private static IReadOnlyList<ImpulseResponse> Trace(ExtendedSource source) => ExtendedSourceTracer.TraceReceivers(Room, source, Receivers, Options);

    [Fact]
    public void AWholeNumberOfBinsOfDelay_MovesTheWholeResponseLater()
    {
        var plain = Trace(ExtendedSource.Point(Position));
        var delayed = Trace(new ExtendedSource(new[] { new SourcePoint(Position, DelayS: 0.010) }));   // bins are 1 ms

        for (var r = 0; r < Receivers.Length; r++)
            for (var b = 0; b < 8; b++)
            {
                var a = plain[r].Energy[b];
                var d = delayed[r].Energy[b];
                for (var i = 0; i < a.Length - 10; i++) Assert.Equal(a[i], d[i + 10]);
                for (var i = 0; i < 10; i++) Assert.Equal(0.0, d[i]);
                Assert.Equal(a.Take(a.Length - 10).Sum(), d.Sum(), 12);
                Assert.Equal(plain[r].LateralEnergy![b].Take(a.Length - 10).Sum(), delayed[r].LateralEnergy![b].Sum(), 12);
            }
    }

    [Fact]
    public void AFractionOfABin_IsSplitBetweenTheTwoItFallsBetween()
    {
        var plain = Trace(ExtendedSource.Point(Position))[0].Energy[4];
        var delayed = Trace(new ExtendedSource(new[] { new SourcePoint(Position, DelayS: 0.0025) }))[0].Energy[4];   // two and a half bins

        for (var j = 3; j < plain.Length - 3; j++)
            Assert.Equal(0.5 * plain[j - 2] + 0.5 * plain[j - 3], delayed[j], 15);
        Assert.Equal(plain.Take(plain.Length - 3).Sum(), delayed.Sum(), 6);   // only what runs off the end is lost
    }

    [Fact]
    public void ADelayStepAlongALine_MovesTheCentreTimeLater_ByAboutTheMeanDelay()
    {
        var from = new Vec3(2, 1, 1.5);
        var to = new Vec3(2, 4, 1.5);
        var even = Trace(ExtendedSource.Line(from, to, 4));
        var stepped = Trace(ExtendedSource.Line(from, to, 4, delaysS: ExtendedSource.StepDelays(4, 0.030)));   // 0, 30, 60 and 90 ms

        for (var r = 0; r < Receivers.Length; r++)
        {
            var before = RoomAcousticsCalculator.Calculate(even[r]);
            var after = RoomAcousticsCalculator.Calculate(stepped[r]);
            var shift = after.CentreTimeS.AtFrequency(1000) - before.CentreTimeS.AtFrequency(1000);
            // the centre time moves by the energy-weighted mean of the delays, which lies between the smallest and the largest
            Assert.InRange(shift, 0.03, 0.06);
            Assert.True(after.C80Db.AtFrequency(1000) < before.C80Db.AtFrequency(1000), "later arrivals leave less of the energy in the first 80 ms");
        }
    }

    [Fact]
    public void WithNoDelays_TheResultIsExactlyWhatItWasBefore()
    {
        var from = new Vec3(2, 1, 1.5);
        var to = new Vec3(2, 4, 1.5);
        var plain = Trace(ExtendedSource.Line(from, to, 3));
        var zero = Trace(ExtendedSource.Line(from, to, 3, delaysS: new[] { 0.0, 0.0, 0.0 }));

        for (var r = 0; r < Receivers.Length; r++)
            for (var b = 0; b < 8; b++)
                Assert.Equal(plain[r].Energy[b], zero[r].Energy[b]);
        Assert.False(ExtendedSource.Line(from, to, 3).HasDelays);
        Assert.True(ExtendedSource.Line(from, to, 3, delaysS: new[] { 0.0, 0.0, 0.001 }).HasDelays);
    }

    [Fact]
    public void StepDelays_CountFromTheStartOfTheLine_AndNeverGoNegative()
    {
        Assert.Equal(new[] { 0.0, 0.01, 0.02, 0.03 }, ExtendedSource.StepDelays(4, 0.01).Select(d => Math.Round(d, 12)));
        Assert.Equal(new[] { 0.03, 0.02, 0.01, 0.0 }, ExtendedSource.StepDelays(4, -0.01).Select(d => Math.Round(d, 12)));
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, ExtendedSource.StepDelays(3, 0));
        Assert.Equal(new[] { 0.0 }, ExtendedSource.StepDelays(1, 0.5));
    }

    [Fact]
    public void WhatCannotBeDelayed_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ExtendedSource(new[] { new SourcePoint(Position, DelayS: -0.001) }));
        Assert.Throws<ArgumentException>(() => new ExtendedSource(new[] { new SourcePoint(Position, DelayS: double.NaN) }));
        Assert.Throws<ArgumentException>(() => ExtendedSource.Line(Position, Position + new Vec3(0, 1, 0), 3, delaysS: new[] { 0.0, 0.1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtendedSource.StepDelays(0, 0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtendedSource.StepDelays(3, double.PositiveInfinity));
    }
}
