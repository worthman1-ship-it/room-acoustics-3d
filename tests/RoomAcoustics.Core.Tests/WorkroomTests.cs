using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The ISO 14257 spatial sound distribution of a workroom: the distribution values, the reference curve, and DL2, DLf and DL'fr in each region.</summary>
public class WorkroomTests
{
    private static OctaveSpectrum FreeFieldG(double r) => OctaveSpectrum.Flat(20 - 20 * Math.Log10(r));

    [Fact]
    public void InAFreeFieldTheCurveIsTheReference()
    {
        // G = 20 - 20 lg r, so D = G - 31 = -20 lg r - 11, Formula 2, in every band, the normalized curve and any machine's spectrum
        foreach (var r in new[] { 1.0, 5, 16 })
        {
            var d = Workroom.Distribution(FreeFieldG(r));
            Assert.Equal(Workroom.Reference(r), d[3], 9);
            // Table 1's Pj sum to 6.25 dB, which the standard rounds to 6.2
            Assert.InRange(Workroom.Normalised(d) - Workroom.Reference(r), 0.04, 0.06);
            Assert.Equal(Workroom.Reference(r), Workroom.ForSpectrum(d, new double[] { 0, 95, 90, 88, 84, 80, 75, 70 }), 9);
        }
        Assert.Equal(-11, Workroom.Reference(1), 9);
    }

    [Fact]
    public void TheRegionsAreTheStandards_NearMiddleAndFar_WithTheirConventionalDistances()
    {
        // 6.2: near from 1 m to d1, middle d1 to d2, far from d2, with d1 = 5 m and d2 = 16 m; 6.4.3: DL'fr at 4 m, 10 m and 30 m
        Assert.Equal(new[] { "near", "middle", "far" }, Workroom.Regions.Select(r => r.Name));
        Assert.Equal((1.0, 5.0, 4.0), (Workroom.Near.FromM, Workroom.Near.ToM, Workroom.Near.AtM));
        Assert.Equal((5.0, 16.0, 10.0), (Workroom.Middle.FromM, Workroom.Middle.ToM, Workroom.Middle.AtM));
        Assert.Equal((16.0, double.PositiveInfinity, 30.0), (Workroom.Far.FromM, Workroom.Far.ToM, Workroom.Far.AtM));
    }

    [Fact]
    public void OtherDistancesForD1AndD2_MoveTheRegionsBoundaries_ButNotTheConventionalDistances()
    {
        // 6.2 allows other d1 and d2 to suit the room (to be reported); 6.4.3's 4 m, 10 m and 30 m stay
        var regions = Workroom.RegionsFor(8, 20);
        Assert.Equal(new[] { (1.0, 8.0, 4.0), (8.0, 20.0, 10.0), (20.0, double.PositiveInfinity, 30.0) }, regions.Select(r => (r.FromM, r.ToM, r.AtM)));
        Assert.Equal(Workroom.Regions, Workroom.RegionsFor(Workroom.D1M, Workroom.D2M));
        foreach (var (d1, d2) in new[] { (1.0, 16.0), (0.5, 16.0), (10.0, 10.0), (12.0, 8.0), (double.NaN, 16.0) })
            Assert.Throws<ArgumentException>(() => Workroom.RegionsFor(d1, d2));
    }

    [Fact]
    public void AFreeFieldFalls6DbPerDoublingWithNoExcess_InEveryRegion()
    {
        double[] r = { 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 25, 32 };
        var d = r.Select(Workroom.Reference).ToList();
        foreach (var region in Workroom.Regions)
        {
            var result = Workroom.Evaluate(r, d, region);
            Assert.Equal(6, result.DecayPerDoublingDb, 9); // Formula 5: -0.3 times the slope against lg r, -20 dB
            Assert.Equal(0, result.ExcessDb, 9);
            // Formula 8 divides DL2 by lg 2 where Formula 5 multiplied by 0.3, so a free field's line sits a few hundredths of a dB off it
            Assert.InRange(result.ExcessAtDb, -0.1, 0.1);
        }
        Assert.Equal(5, Workroom.Evaluate(r, d, Workroom.Near).Positions);   // 1 to 5 m
        Assert.Equal(6, Workroom.Evaluate(r, d, Workroom.Middle).Positions); // 5 to 16 m
        Assert.Equal(4, Workroom.Evaluate(r, d, Workroom.Far).Positions);    // 16 m on
    }

    [Fact]
    public void TheDecayIsFormula5_TheExcessFormula7_AndTheExcessAtADistanceFormula8()
    {
        // positions 5, 7, 10 and 16 m with D off a straight line, so the regression and the averaging both matter
        double[] r = { 5, 7, 10, 16 };
        double[] d = { -21.0, -22.5, -23.2, -25.6 };
        var result = Workroom.Evaluate(r, d, Workroom.Middle);

        var x = r.Select(Math.Log10).ToArray();
        var z = r.Length;
        var slope = (z * x.Zip(d, (a, b) => a * b).Sum() - d.Sum() * x.Sum()) / (z * x.Sum(a => a * a) - x.Sum() * x.Sum());
        Assert.Equal(-0.3 * slope, result.DecayPerDoublingDb, 9);

        // the excess averaged over lg r by trapezoids: each step's two ends, weighted by the step's width in lg r
        var e = r.Zip(d, (ri, di) => di - Workroom.Reference(ri)).ToArray();
        var area = Enumerable.Range(1, z - 1).Sum(i => (e[i] + e[i - 1]) * Math.Log10(r[i] / r[i - 1]));
        Assert.Equal(area / (2 * Math.Log10(16.0 / 5)), result.ExcessDb, 9);
        Assert.NotEqual(e.Average(), result.ExcessDb, 3); // not the plain mean

        // the regression line's height over the free-field line at 10 m, as Formula 8 writes it
        var at = d.Average() + 20 * Math.Log10(10) + result.DecayPerDoublingDb / Math.Log10(2) * (x.Average() - Math.Log10(10)) + 11;
        Assert.Equal(at, result.ExcessAtDb, 9);
    }

    [Fact]
    public void TheOrderOfThePositionsDoesNotMatter()
    {
        double[] r = { 16, 5, 10, 7 };
        double[] d = { -25.6, -21.0, -23.2, -22.5 };
        var sorted = Workroom.Evaluate(new double[] { 5, 7, 10, 16 }, new[] { -21.0, -22.5, -23.2, -25.6 }, Workroom.Middle);
        Assert.Equal(sorted, Workroom.Evaluate(r, d, Workroom.Middle));
    }

    [Fact]
    public void AReverberantRoomDecaysSlowerAndLiesAboveTheReference()
    {
        // D = -2 dB per doubling from -20 dB at 5 m: a slope of -2 / lg 2 against lg r, so DL2 = 0.3 x 2 / lg 2, just under 2
        double[] r = { 5, 10, 16 };
        var values = r.Select(x => -20 - 2 * Math.Log2(x / 5)).ToList();
        var result = Workroom.Evaluate(r, values, Workroom.Middle);
        Assert.Equal(0.3 * 2 / Math.Log10(2), result.DecayPerDoublingDb, 9);
        Assert.True(result.ExcessDb > 0);
        Assert.True(result.ExcessAtDb > 0);
    }

    [Fact]
    public void NothingIsWorkedOutWithFewerThanTwoPositionsInTheRegion()
    {
        var result = Workroom.Evaluate(new[] { 2.0, 4, 8 }, new[] { -20.0, -22, -24 }, Workroom.Middle);
        Assert.True(double.IsNaN(result.DecayPerDoublingDb));
        Assert.True(double.IsNaN(result.ExcessDb));
        Assert.True(double.IsNaN(result.ExcessAtDb));
        Assert.Equal(1, result.Positions);
    }
}
