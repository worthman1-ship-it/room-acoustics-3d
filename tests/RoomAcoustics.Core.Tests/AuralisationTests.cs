using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class AuralisationTests
{
    private static ImpulseResponse Ir(Func<int, double> energyPerBin, int bins, double dt = 0.001)
    {
        var band = Enumerable.Range(0, bins).Select(energyPerBin).ToArray();
        return new ImpulseResponse(dt, Enumerable.Repeat(band, 8).ToArray(), OctaveSpectrum.Flat(1));
    }

    [Fact]
    public void Convolve_MatchesTheDirectSum()
    {
        double[] x = { 1, 2, 3 };
        double[] h = { 0.5, -1, 2, 0.25 };

        var result = Auralisation.Convolve(x, h);

        var expected = new double[x.Length + h.Length - 1];
        for (var i = 0; i < x.Length; i++)
            for (var j = 0; j < h.Length; j++)
                expected[i + j] += x[i] * h[j];
        Assert.Equal(expected.Length, result.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], result[i], 9);
    }

    [Fact]
    public void Convolve_WithAUnitImpulse_ReturnsTheSignal()
    {
        double[] x = { 0.1, -0.4, 0.7, 0.2, -0.9 };

        var result = Auralisation.Convolve(x, new double[] { 1 });

        Assert.Equal(x, result.Take(x.Length).ToArray(), new DoubleArrayComparer(1e-9));
    }

    [Fact]
    public void EarPositions_SitAcrossTheHeadPerpendicularToTheFacing()
    {
        // Facing +y, the listener's right is +x.
        var (left, right) = Auralisation.EarPositions(new Vec3(5, 3, 1.2), new Vec3(0, 1, 0), 0.17);

        Assert.Equal(4.915, left.X, 9);
        Assert.Equal(5.085, right.X, 9);
        Assert.Equal(3, left.Y, 9);
        Assert.Equal(1.2, right.Z, 9);
    }

    [Fact]
    public void SynthesizedImpulseResponse_KeepsTheEnergyEnvelope()
    {
        // Energy 1 per bin for the first half second, and 0.1 per bin for the second half.
        var ir = Ir(i => i < 500 ? 1.0 : 0.1, 1000);

        var h = Auralisation.SynthesizeImpulseResponse(ir, 48_000, seed: 3);

        var first = h.Take(24_000).Sum(x => x * x);
        var second = h.Skip(24_000).Take(24_000).Sum(x => x * x);
        Assert.InRange(first / second, 9.0, 11.0); // a factor of 10 in energy, within noise
    }

    [Fact]
    public void SynthesizedImpulseResponse_IsSilentBeforeTheFirstEnergy()
    {
        var ir = Ir(i => i == 100 ? 1.0 : 0.0, 1000);

        var h = Auralisation.SynthesizeImpulseResponse(ir, 48_000, seed: 3);

        var before = h.Take(4_800).Sum(x => x * x); // first 100 ms, before the bin at 100 ms
        var total = h.Sum(x => x * x);
        Assert.True(before < 1e-6 * total, "No sound should come before the energy arrives.");
    }

    [Fact]
    public void RenderStereo_ScalesBothEarsByTheSameFactor()
    {
        double[] dry = { 1, 0, 0, 0 };
        double[] left = { 0.8, 0, 0 };
        double[] right = { 0.4, 0, 0 };

        var (l, r) = Auralisation.RenderStereo(dry, left, right, peak: 0.5);

        Assert.Equal(0.5, l.Max(Math.Abs), 9);
        Assert.Equal(0.25, r.Max(Math.Abs), 9); // the ratio between ears is kept
    }

    [Fact]
    public void Stereo_SourceOnTheLeft_ReachesTheLeftEarFirstAndLouder()
    {
        // Listener at (6, 3.5) facing +y, source 4 m to the left. The left ear is closer, so it hears the direct sound first.
        var room = Room3D.Shoebox(10, 7, 3,
            new SurfaceFinish(new AbsorptionMaterial("T", "T", Enumerable.Repeat((double?)0.2, 8).ToArray())));
        var (leftEar, rightEar) = Auralisation.EarPositions(new Vec3(6, 3.5, 1.2), new Vec3(0, 1, 0));
        var source = new Vec3(2, 3.5, 1.2);
        var options = new RayTraceOptions(RayCount: 20_000, ReceiverRadiusM: 0.25, MaxTimeS: 0.05, BinWidthS: 0.0001);

        var ears = EnergyRayTracer.TraceReceivers(room, source, new[] { leftEar, rightEar }, options);

        var leftFirst = FirstNonZeroBin(ears[0].Energy[4]);
        var rightFirst = FirstNonZeroBin(ears[1].Energy[4]);
        Assert.True(leftFirst < rightFirst, $"Left ear first sound at bin {leftFirst}, right at {rightFirst}.");
        Assert.True(ears[0].Energy[4][leftFirst] > ears[1].Energy[4][rightFirst],
            "The near (left) ear should receive more direct energy.");
    }

    private static int FirstNonZeroBin(double[] bins)
    {
        for (var i = 0; i < bins.Length; i++)
            if (bins[i] > 0) return i;
        return -1;
    }

    private sealed class DoubleArrayComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(double obj) => 0;
    }
}
