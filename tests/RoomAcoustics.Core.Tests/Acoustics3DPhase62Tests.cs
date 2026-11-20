using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class ArrayShadingTests
{
    private const int TwoKHz = 5;   // band index of 2 kHz

    /// <summary>The highest level, relative to the level straight ahead, at elevations beyond the main lobe (25 degrees and more either side).</summary>
    private static double SideLobeDb(BalloonDirectivity b)
    {
        var peak = b.LevelDb(TwoKHz, 0, 0);
        var worst = double.NegativeInfinity;
        for (var el = 25; el <= 90; el += 5)
            worst = Math.Max(worst, Math.Max(b.LevelDb(TwoKHz, 0, el), b.LevelDb(TwoKHz, 0, -el)) - peak);
        return worst;
    }

    private static double HalfPowerWidth(BalloonDirectivity b)
    {
        var top = b.LevelDb(TwoKHz, 0, 0);
        double width = 5;
        for (var el = 5; el <= 90 && b.LevelDb(TwoKHz, 0, el) >= top - 3; el += 5) width += 5;
        for (var el = -5; el >= -90 && b.LevelDb(TwoKHz, 0, el) >= top - 3; el -= 5) width += 5;
        return width;
    }

    [Theory]
    [InlineData(ArrayShading.Uniform)]
    [InlineData(ArrayShading.Hamming)]
    [InlineData(ArrayShading.Hann)]
    [InlineData(ArrayShading.Sine)]
    public void TheWeights_AreSymmetric_PositiveOrZero_AndTheLargestIsOne(ArrayShading shading)
    {
        foreach (var n in new[] { 1, 2, 5, 8, 13 })
        {
            var w = CoherentLineArray.ShadingWeights(shading, n);
            Assert.Equal(n, w.Length);
            Assert.Equal(1, w.Max(), 9);
            Assert.All(w, x => Assert.InRange(x, 0, 1));
            for (var i = 0; i < n; i++) Assert.Equal(w[i], w[n - 1 - i], 9);
        }
    }

    [Fact]
    public void Uniform_IsAllOnes_AndTheTapersAreSmallerAtTheEndsThanInTheMiddle()
    {
        Assert.All(CoherentLineArray.ShadingWeights(ArrayShading.Uniform, 7), x => Assert.Equal(1, x));
        foreach (var shading in new[] { ArrayShading.Hamming, ArrayShading.Hann, ArrayShading.Sine })
        {
            var w = CoherentLineArray.ShadingWeights(shading, 9);
            Assert.True(w[0] < w[4]);
            Assert.True(w[0] < 0.5);
        }
        Assert.Equal(0.08 / 1.0, CoherentLineArray.ShadingWeights(ArrayShading.Hamming, 9)[0], 9);
    }

    [Fact]
    public void ShadingWeights_RefuseNoElementsAndUnknownShadings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ShadingWeights(ArrayShading.Uniform, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ShadingWeights((ArrayShading)99, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical, shading: (ArrayShading)99));
    }

    [Fact]
    public void LeavingShadingOut_GivesTheSameArrayAsUniform()
    {
        var plain = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical);
        var uniform = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical, shading: ArrayShading.Uniform);
        for (var el = -90; el <= 90; el += 15)
            Assert.Equal(plain.LevelDb(TwoKHz, 0, el), uniform.LevelDb(TwoKHz, 0, el), 9);
    }

    [Fact]
    public void ATaper_LowersTheSideLobes_AndWidensTheMainBeam()
    {
        var uniform = CoherentLineArray.ToBalloon(12, 0.1, ArrayOrientation.Vertical);
        var uniformSides = SideLobeDb(uniform);
        var uniformWidth = HalfPowerWidth(uniform);
        foreach (var shading in new[] { ArrayShading.Hamming, ArrayShading.Hann })
        {
            var tapered = CoherentLineArray.ToBalloon(12, 0.1, ArrayOrientation.Vertical, shading: shading);
            Assert.True(SideLobeDb(tapered) < uniformSides - 6, $"{shading}: {SideLobeDb(tapered):0.0} dB against {uniformSides:0.0} dB");
            Assert.True(HalfPowerWidth(tapered) > uniformWidth);
        }
    }

    [Fact]
    public void ATaper_StillPointsTheSameWay_WhenTheArrayIsSteered()
    {
        var tau = 0.1 * Math.Sin(20 * Math.PI / 180) / 343;
        var tapered = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical, tau, shading: ArrayShading.Hamming);
        var best = Enumerable.Range(-18, 37).Select(i => i * 5).MaxBy(el => tapered.LevelDb(TwoKHz, 0, el));
        Assert.InRange(best, 15, 25);
    }
}

public class ChebyshevShadingTests
{
    private const int TwoKHz = 5;

    private static double SideLobeDb(BalloonDirectivity b, double fromDeg)
    {
        var peak = b.LevelDb(TwoKHz, 0, 0);
        var worst = double.NegativeInfinity;
        for (var el = fromDeg; el <= 90; el += 2.5)
            worst = Math.Max(worst, Math.Max(b.LevelDb(TwoKHz, 0, el), b.LevelDb(TwoKHz, 0, -el)) - peak);
        return worst;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    public void TheWeights_AreSymmetric_PositiveAndLargestInTheMiddle(int n)
    {
        var w = CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, n, 30);
        Assert.Equal(1, w.Max(), 9);
        for (var i = 0; i < n; i++) { Assert.Equal(w[i], w[n - 1 - i], 9); Assert.True(w[i] > 0); }
        Assert.True(w[0] < w[n / 2]);
    }

    [Fact]
    public void ThreeElementsAt20Db_MatchTheClosedForm()
    {
        // three elements (a, b, a): b + 2a cos(psi) = T2(x0 cos(psi/2)) = (x0^2 - 1) + x0^2 cos(psi), so a/b = x0^2 / (2 (x0^2 - 1))
        var w = CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, 3, 20);
        var x0Squared = (1 + 10.0) / 2;   // cosh^2(acosh(R)/2) = (1 + R)/2 for R = 10, 20 dB as an amplitude ratio
        Assert.Equal(x0Squared / (2 * (x0Squared - 1)), w[0] / w[1], 6);
    }

    [Fact]
    public void ADeeperSideLobeLevel_GivesMoreTaper()
    {
        var shallow = CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, 10, 20);
        var deep = CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, 10, 50);
        Assert.True(deep[0] < shallow[0]);
    }

    [Fact]
    public void EveryRemoteSideLobe_IsHeldAtOrBelowTheChosenLevel()
    {
        var uniform = CoherentLineArray.ToBalloon(12, 0.1, ArrayOrientation.Vertical);
        var cheb = CoherentLineArray.ToBalloon(12, 0.1, ArrayOrientation.Vertical, shading: ArrayShading.Chebyshev, chebyshevSidelobeDb: 30);
        Assert.True(SideLobeDb(uniform, 40) > -25);
        Assert.True(SideLobeDb(cheb, 40) < -28, $"{SideLobeDb(cheb, 40):0.0} dB");
    }

    [Fact]
    public void SmallArrays_AreEqualWeights_AndBadLevelsAreRefused()
    {
        Assert.All(CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, 2, 30), x => Assert.Equal(1, x));
        Assert.Equal(new[] { 1.0 }, CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, 1, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, 8, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ShadingWeights(ArrayShading.Chebyshev, 8, double.NaN));
    }
}
