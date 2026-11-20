using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class AmbisonicsTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0.1));

    private static double Energy(double[] c, int from = 0, int to = int.MaxValue) => c.Skip(from).Take(to - from).Sum(v => v * v);

    [Theory]
    [InlineData(4.0, 3.0, 1.5, 3)]  // from +x: X (ACN 3) carries it
    [InlineData(2.0, 5.0, 1.5, 1)]  // from +y: Y (ACN 1)
    [InlineData(2.0, 3.0, 4.0, 2)]  // from above: Z (ACN 2)
    public void InARoomThatReflectsNothing_TheDirectSoundIsAPlaneWaveFromTheSource(double sx, double sy, double sz, int channel)
    {
        var room = Room3D.Shoebox(8, 6, 5, Finish(1));
        var source = new ExtendedSource(new[] { new SourcePoint(new Vec3(sx, sy, sz)) });
        var b = Ambisonics.Render(room, source, new Vec3(2, 3, 1.5), 16000, new RayTraceOptions(RayCount: 2000, MaxTimeS: 0.2));
        Assert.Equal(4, b.Length);
        var w = Energy(b[0]);
        Assert.True(w > 0);
        // all of it in the one channel facing the source, as much as in W, and none in the others
        Assert.InRange(Energy(b[channel]) / w, 0.99, 1.01);
        for (var k = 1; k < 4; k++) if (k != channel) Assert.True(Energy(b[k]) / w < 1e-6);
    }

    [Fact]
    public void TheLateTail_IsDiffuse_AThirdOfWsEnergyInEachOfXYAndZ()
    {
        var room = Room3D.Shoebox(10, 7, 4, Finish(0.05));
        var source = new ExtendedSource(new[] { new SourcePoint(new Vec3(2, 2, 1.5)) });
        const int rate = 16000;
        var b = Ambisonics.Render(room, source, new Vec3(7, 5, 1.6), rate, new RayTraceOptions(RayCount: 40_000, ImageSourceOrder: 2, MaxTimeS: 1.0));
        // from 0.3 s on, the sound comes from every way
        var from = (int)(0.3 * rate);
        var w = Energy(b[0], from);
        for (var k = 1; k < 4; k++) Assert.InRange(Energy(b[k], from) / w, 0.25, 0.42);
        Assert.InRange((Energy(b[1], from) + Energy(b[2], from) + Energy(b[3], from)) / w, 0.85, 1.15);
    }

    [Fact]
    public void TheHarmonics_AreSn3d_EachOfOrderNHoldingAThirdOrAFifthOrASeventhOfWsEnergyOverTheSphere_AndOrthogonal()
    {
        // a Fibonacci lattice: the mean of Y_j Y_k over the sphere is 1 / (2n + 1) for j = k of order n, 0 otherwise
        const int n = 20000;
        var golden = Math.PI * (3 - Math.Sqrt(5));
        var sums = new double[16, 16];
        for (var i = 0; i < n; i++)
        {
            var z = 1 - 2 * (i + 0.5) / n;
            var r = Math.Sqrt(1 - z * z);
            var y = Ambisonics.Harmonics(new Vec3(r * Math.Cos(i * golden), r * Math.Sin(i * golden), z), 3);
            for (var j = 0; j < 16; j++) for (var k = 0; k < 16; k++) sums[j, k] += y[j] * y[k] / n;
        }
        for (var j = 0; j < 16; j++)
            for (var k = 0; k < 16; k++)
                Assert.Equal(j == k ? 1.0 / (2 * (int)Math.Sqrt(j) + 1) : 0, sums[j, k], 3);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AtHigherOrder_TheDirectSoundIsAPlaneWave_EachChannelItsHarmonicTowardsTheSource(int order)
    {
        var room = Room3D.Shoebox(8, 6, 5, Finish(1));
        var source = new ExtendedSource(new[] { new SourcePoint(new Vec3(5, 5, 3)) });
        var point = new Vec3(2, 3, 1.5);
        var b = Ambisonics.Render(room, source, point, 16000, new RayTraceOptions(RayCount: 2000, MaxTimeS: 0.2), order: order);
        Assert.Equal((order + 1) * (order + 1), b.Length);
        var gains = Ambisonics.Harmonics((new Vec3(5, 5, 3) - point).Normalized(), order);
        var w = Energy(b[0]);
        for (var k = 1; k < b.Length; k++) Assert.Equal(gains[k] * gains[k], Energy(b[k]) / w, 3);
    }

    [Fact]
    public void AtThirdOrder_TheLateTail_HasAFifthOfWsEnergyInEachSecondOrderChannel_AndASeventhInEachThird()
    {
        var room = Room3D.Shoebox(10, 7, 4, Finish(0.05));
        var source = new ExtendedSource(new[] { new SourcePoint(new Vec3(2, 2, 1.5)) });
        const int rate = 16000;
        var b = Ambisonics.Render(room, source, new Vec3(7, 5, 1.6), rate, new RayTraceOptions(RayCount: 40_000, ImageSourceOrder: 2, MaxTimeS: 1.0), order: 3);
        var from = (int)(0.3 * rate);
        var w = Energy(b[0], from);
        for (var k = 4; k < 9; k++) Assert.InRange(Energy(b[k], from) / w, 0.13, 0.27);
        for (var k = 9; k < 16; k++) Assert.InRange(Energy(b[k], from) / w, 0.09, 0.2);
    }

    [Fact]
    public void AResponseTracedWithoutDirections_CannotBeMadeAmbisonic()
    {
        var room = Room3D.Shoebox(8, 6, 5, Finish(0.3));
        var ir = EnergyRayTracer.Trace(room, new Vec3(2, 3, 1.5), new Vec3(6, 3, 1.5), new RayTraceOptions(RayCount: 500, MaxTimeS: 0.2));
        Assert.Throws<ArgumentException>(() => Ambisonics.Synthesize(ir, 16000));
    }
}
