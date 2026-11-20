using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>Sources with spectra of their own: each octave band's response is shared between the sources by their power in that band.</summary>
public sealed class SourceBandGainTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    private static Room3D Room() => Room3D.Shoebox(8, 6, 3, Finish(0.3));

    private static readonly Vec3 A = new(1, 3, 1.5), B = new(7, 3, 1.5), Ear = new(2, 3, 1.5);

    private static RayTraceOptions Quick() => new(RayCount: 20_000, ImageSourceOrder: 1);

    [Fact]
    public void TheCentreFollowsEachSourcesPower_NotItsWeightAlone()
    {
        var loud = Enumerable.Repeat(1e10, 8).ToArray();  // 100 dB
        var quiet = Enumerable.Repeat(1e6, 8).ToArray();  // 60 dB
        var source = new ExtendedSource(new[] { new SourcePoint(A, 1, null, 0, loud), new SourcePoint(B, 1, null, 0, quiet) });
        Assert.True((source.Centre - A).Length < 0.01, "the centre sits on the loud source");

        // with no band gains it goes by the weights, as before
        var even = new ExtendedSource(new[] { new SourcePoint(A), new SourcePoint(B) });
        Assert.True((even.Centre - new Vec3(4, 3, 1.5)).Length < 1e-9);
    }

    /// <summary>A is the loud one in band 3 (500 Hz) only; B is the loud one in every other band.</summary>
    private static readonly double[] LoudInBand3 = Enumerable.Range(0, 8).Select(b => b == 3 ? 1.0 : 1e-6).ToArray();
    private static readonly double[] LoudElsewhere = Enumerable.Range(0, 8).Select(b => b == 3 ? 1e-6 : 1.0).ToArray();

    [Fact]
    public void WithoutBandGains_TheSharesAreTheWeightsOverTheirSum_InEveryBand()
    {
        var shares = new ExtendedSource(new[] { new SourcePoint(A, 1), new SourcePoint(B, 3) }).SharesByBand();
        for (var b = 0; b < 8; b++)
        {
            Assert.Equal(0.25, shares[0][b], 12);
            Assert.Equal(0.75, shares[1][b], 12);
        }
    }

    [Fact]
    public void WithBandGains_EachBandIsSharedByThePowerInThatBand()
    {
        var source = new ExtendedSource(new[] { new SourcePoint(A, 1, null, 0, new double[] { 1, 1, 1, 1, 4, 1, 1, 1 }), new SourcePoint(B, 1) });
        var shares = source.SharesByBand();
        Assert.Equal(0.5, shares[0][0], 12);
        Assert.Equal(0.8, shares[0][4], 12);
        Assert.Equal(0.2, shares[1][4], 12);
        for (var b = 0; b < 8; b++) Assert.Equal(1.0, shares[0][b] + shares[1][b], 12);
    }

    [Fact]
    public void BandGainsThatAreNotEightPositiveNumbers_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new ExtendedSource(new[] { new SourcePoint(A, 1, null, 0, new double[] { 1, 1 }), new SourcePoint(B) }));
        Assert.Throws<ArgumentException>(() => new ExtendedSource(new[] { new SourcePoint(A, 1, null, 0, new double[] { 1, 1, 1, 1, 1, 1, 1, 0 }), new SourcePoint(B) }));
    }

    [Fact]
    public void EachBandOfTheResponse_FollowsTheSourceThatIsLoudInIt()
    {
        var room = Room();
        var two = ExtendedSourceTracer.TraceReceivers(room,
            new ExtendedSource(new[] { new SourcePoint(A, 1, null, 0, LoudInBand3), new SourcePoint(B, 1, null, 0, LoudElsewhere) }), new[] { Ear }, Quick())[0];
        var onlyA = EnergyRayTracer.TraceReceivers(room, A, new[] { Ear }, Quick())[0];
        var onlyB = EnergyRayTracer.TraceReceivers(room, B, new[] { Ear }, Quick())[0];

        // 500 Hz is A's alone (a metre from the ear); every other band is B's alone (five metres away), and the two are very different in level
        Assert.InRange(two.Energy[3].Sum() / onlyA.Energy[3].Sum(), 0.85, 1.15);
        foreach (var band in new[] { 0, 2, 4, 7 })
            Assert.InRange(two.Energy[band].Sum() / onlyB.Energy[band].Sum(), 0.85, 1.15);
        Assert.True(onlyA.Energy[3].Sum() > 3 * onlyB.Energy[3].Sum(), "the two sources really do differ in this band");
    }
}
