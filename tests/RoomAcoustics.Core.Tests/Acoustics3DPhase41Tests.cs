using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class CoherentLineArrayTests
{
    private const double C = 343;
    private const int KHz = 4, TwoKHz = 5, FiveHundred = 3;   // band indices: 1 kHz, 2 kHz, 500 Hz

    private static double Level(BalloonDirectivity b, int band, double az, double el) => b.LevelDb(band, az, el);

    /// <summary>The elevation (or azimuth) of the strongest direction on the given cut.</summary>
    private static double PeakElevation(BalloonDirectivity b, int band, double az = 0)
    {
        var best = double.NegativeInfinity;
        var at = 0.0;
        for (var el = -90; el <= 90; el += 5)
        {
            var level = Level(b, band, az, el);
            if (level > best) { best = level; at = el; }
        }
        return at;
    }

    /// <summary>How wide, in degrees on the cut through the peak, the level is within 3 dB of the peak (a contiguous run round the peak).</summary>
    private static double HalfPowerWidth(BalloonDirectivity b, int band)
    {
        var peak = PeakElevation(b, band);
        var top = Level(b, band, 0, peak);
        double width = 5;
        for (var el = peak + 5; el <= 90 && Level(b, band, 0, el) >= top - 3; el += 5) width += 5;
        for (var el = peak - 5; el >= -90 && Level(b, band, 0, el) >= top - 3; el -= 5) width += 5;
        return width;
    }

    [Fact]
    public void AVerticalUniformArray_PointsStraightAhead_AndIsTheSameAllRoundTheLine()
    {
        var array = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical);

        Assert.Equal(0, PeakElevation(array, KHz));
        Assert.True(Level(array, KHz, 0, 0) > Level(array, KHz, 0, 50) + 6);
        // the line is vertical, so the pattern depends on the elevation alone: every azimuth sees the same level at the same elevation
        foreach (var az in new[] { 45.0, 90, 180, 270 })
            Assert.Equal(Level(array, KHz, 0, 30), Level(array, KHz, az, 30), 6);
    }

    [Fact]
    public void ALongerArray_HasANarrowerBeam_AndAHigherFrequencyNarrowsIt()
    {
        var short4 = CoherentLineArray.ToBalloon(4, 0.1, ArrayOrientation.Vertical);
        var long12 = CoherentLineArray.ToBalloon(12, 0.1, ArrayOrientation.Vertical);

        Assert.True(HalfPowerWidth(long12, TwoKHz) < HalfPowerWidth(short4, TwoKHz));
        Assert.True(HalfPowerWidth(long12, TwoKHz) < HalfPowerWidth(long12, FiveHundred));
        // roughly 0.886 lambda / L for the half-power width of a uniform line: 12 x 0.1 m at 2 kHz is 0.886 * 0.1715 / 1.2 rad, about 7 degrees
        Assert.InRange(HalfPowerWidth(long12, TwoKHz), 5, 16);
    }

    [Fact]
    public void ADelayStep_SteersTheBeamTowardTheLaterEnd_BySinThetaEqualsCTauOverD()
    {
        const double d = 0.1;
        foreach (var degrees in new[] { 10.0, 20, 30 })
        {
            var tau = d * Math.Sin(degrees * Math.PI / 180) / C;
            var up = CoherentLineArray.ToBalloon(8, d, ArrayOrientation.Vertical, tau);
            var down = CoherentLineArray.ToBalloon(8, d, ArrayOrientation.Vertical, -tau);

            Assert.InRange(PeakElevation(up, KHz), degrees - 5, degrees + 5);
            Assert.InRange(PeakElevation(down, KHz), -degrees - 5, -degrees + 5);
        }
    }

    [Fact]
    public void ReversingTheDelay_MirrorsThePatternAboutTheHorizontal()
    {
        var tau = 0.1 * Math.Sin(15 * Math.PI / 180) / C;
        var up = CoherentLineArray.ToBalloon(6, 0.1, ArrayOrientation.Vertical, tau);
        var down = CoherentLineArray.ToBalloon(6, 0.1, ArrayOrientation.Vertical, -tau);

        foreach (var el in new[] { -60.0, -25, 0, 15, 40, 85 })
            Assert.Equal(Level(up, TwoKHz, 0, el), Level(down, TwoKHz, 0, -el), 6);
    }

    [Fact]
    public void AHorizontalArray_ShapesTheAzimuth_AndIsFlatInElevationOnTheAxis()
    {
        var array = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Horizontal);

        Assert.True(Level(array, KHz, 0, 0) > Level(array, KHz, 60, 0) + 6);
        Assert.True(Level(array, KHz, 0, 0) > Level(array, KHz, 300, 0) + 6);
        Assert.Equal(Level(array, KHz, 60, 0), Level(array, KHz, 300, 0), 6);                // symmetric left and right
        foreach (var el in new[] { -60.0, -20, 30, 75 })
            Assert.Equal(Level(array, KHz, 0, 0), Level(array, KHz, 0, el), 6);               // straight ahead, the line adds nothing in elevation
    }

    [Fact]
    public void AHorizontalDelay_SteersToTheLeft_ForAPositiveStep()
    {
        var tau = 0.1 * Math.Sin(20 * Math.PI / 180) / C;
        var array = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Horizontal, tau);

        var best = double.NegativeInfinity;
        var at = 0.0;
        for (var az = 0; az < 360; az += 5)
            if (Level(array, KHz, az, 0) > best) { best = Level(array, KHz, az, 0); at = az; }
        Assert.InRange(at, 15, 25);                                                          // left is +y, azimuth increasing toward 90
    }

    [Fact]
    public void WidelySpacedElements_GrowGratingLobes_ThatCloseSpacingDoesNot()
    {
        // 1 kHz band (lambda 24 to 49 cm): d = 0.4 m is over a wavelength, so lobes stand where d sin(theta) = n lambda; d = 0.1 m is under half the shortest
        var wide = CoherentLineArray.ToBalloon(8, 0.4, ArrayOrientation.Vertical);
        var close = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical);

        double OffAxisPeak(BalloonDirectivity b)
        {
            var best = double.NegativeInfinity;
            for (var el = 30; el <= 90; el += 5) best = Math.Max(best, Level(b, KHz, 0, el));
            return best - Level(b, KHz, 0, 0);
        }
        Assert.True(OffAxisPeak(wide) > -9);                                                 // strong secondary lobes
        Assert.True(OffAxisPeak(close) < -12);                                               // d under half a wavelength: none
    }

    [Fact]
    public void DirectionalElementsSplayedOut_CoverAWiderAngleThanTheSameElementsAimedTogether()
    {
        var element = BalloonDirectivity.FromCoverage(120, 30, -30);
        var straight = CoherentLineArray.ToBalloon(6, 0.15, ArrayOrientation.Vertical, 0, 0, element);
        var splayed = CoherentLineArray.ToBalloon(6, 0.15, ArrayOrientation.Vertical, 0, 8, element);

        double Drop(BalloonDirectivity b, double el) => Level(b, FiveHundred, 0, 0) - Level(b, FiveHundred, 0, el);
        Assert.True(Drop(splayed, 25) < Drop(straight, 25) - 3);
        Assert.True(Drop(splayed, -25) < Drop(straight, -25) - 3);
        Assert.Equal(Level(splayed, FiveHundred, 0, 25), Level(splayed, FiveHundred, 0, -25), 3);   // an even fan is symmetric
    }

    [Fact]
    public void TheElementPattern_ShapesTheArray_AndASingleElementIsJustItsOwnPattern()
    {
        var element = BalloonDirectivity.FromCoverage(90, 40, -20);
        var omni = CoherentLineArray.ToBalloon(6, 0.1, ArrayOrientation.Vertical);
        var directional = CoherentLineArray.ToBalloon(6, 0.1, ArrayOrientation.Vertical, 0, 0, element);

        // behind the array the omni elements still radiate; the directional ones do not
        Assert.True(Level(directional, KHz, 0, 0) - Level(directional, KHz, 180, 0) > Level(omni, KHz, 0, 0) - Level(omni, KHz, 180, 0) + 10);
    }

    [Fact]
    public void ALevelNeverFallsMoreThanSixtyDecibelsUnderThePeak_AndTheGridIsComplete()
    {
        var array = CoherentLineArray.ToBalloon(16, 0.2, ArrayOrientation.Vertical);

        for (var band = 0; band < 8; band++)
        {
            var peak = double.NegativeInfinity;
            var low = double.PositiveInfinity;
            foreach (var az in array.AzimuthsDeg)
                foreach (var el in array.ElevationsDeg)
                {
                    var level = Level(array, band, az, el);
                    peak = Math.Max(peak, level);
                    low = Math.Min(low, level);
                }
            Assert.True(peak - low <= CoherentLineArray.FloorBelowPeakDb + 1e-6);
        }
        Assert.Equal(72, array.AzimuthsDeg.Count);
        Assert.Equal(37, array.ElevationsDeg.Count);
        Assert.Equal(-90, array.ElevationsDeg[0]);
        Assert.Equal(90, array.ElevationsDeg[^1]);
    }

    [Fact]
    public void TheArray_CanBeAimedAndUsedLikeAnyBalloon()
    {
        var array = CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Horizontal);

        var aimed = array.Aimed(new Vec3(0, 1, 0));                                           // facing +y

        Assert.True(aimed.Gain(new Vec3(0, 1, 0)) > aimed.Gain(new Vec3(1, 0, 0)));           // along its front, not off to the side
        Assert.True(aimed.Gain(new Vec3(0, 1, 0)) > aimed.Gain(new Vec3(0.6, 0.8, 0)));
    }

    [Fact]
    public void ABadArray_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(1, 0.1, ArrayOrientation.Vertical));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(129, 0.1, ArrayOrientation.Vertical));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(8, 0, ArrayOrientation.Vertical));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(8, double.NaN, ArrayOrientation.Vertical));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical, 0, 40));      // 140 degrees at the ends
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical, 0, 0, null, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherentLineArray.ToBalloon(8, 0.1, ArrayOrientation.Vertical, 0, 0, null, 45));
    }
}
