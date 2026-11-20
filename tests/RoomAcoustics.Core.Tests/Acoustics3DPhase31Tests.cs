using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class StageSupportTests
{
    private const double Dt = 0.001;

    /// <summary>An energy response with the given (time s, energy) arrivals in every band, and a unit free-field reference.</summary>
    private static ImpulseResponse Arrivals(params (double TimeS, double Energy)[] arrivals)
    {
        var h = new double[2000];
        foreach (var (t, e) in arrivals) h[(int)Math.Round(t / Dt)] += e;
        return new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));
    }

    [Fact]
    public void TheEarlyAndLateSupport_AreTheEnergyAfterTheDirectSoundOverTheFirst10Ms()
    {
        // direct at 3 ms and a floor reflection at 8 ms are the "first 10 ms"; 40 ms is early support; 300 ms is late support
        var m = RoomAcousticsCalculator.Calculate(Arrivals((0.003, 1.0), (0.008, 0.5), (0.040, 0.2), (0.300, 0.1)));

        for (var band = 0; band < 8; band++)
        {
            Assert.Equal(10 * Math.Log10(0.2 / 1.5), m.StageSupportEarlyDb[band], 9);
            Assert.Equal(10 * Math.Log10(0.1 / 1.5), m.StageSupportLateDb[band], 9);
        }
    }

    [Fact]
    public void TheTimesRunFromTheDirectSound_NotFromTheEmission()
    {
        var near = RoomAcousticsCalculator.Calculate(Arrivals((0.003, 1.0), (0.040, 0.2), (0.300, 0.1)));
        var far = RoomAcousticsCalculator.Calculate(Arrivals((0.053, 1.0), (0.090, 0.2), (0.350, 0.1)));    // everything 50 ms later

        Assert.Equal(near.StageSupportEarlyDb[3], far.StageSupportEarlyDb[3], 9);
        Assert.Equal(near.StageSupportLateDb[3], far.StageSupportLateDb[3], 9);
    }

    [Fact]
    public void TheWindowsStartAndEndWhereGadePutsThem()
    {
        var direct = (0.005, 1.0);

        // 15 ms after the direct sound is in neither window (the gap between 10 and 20 ms), 20 ms is in the early one
        var gap = RoomAcousticsCalculator.Calculate(Arrivals(direct, (0.020, 0.5)));
        Assert.True(double.IsNaN(gap.StageSupportEarlyDb[2]) && double.IsNaN(gap.StageSupportLateDb[2]));
        var atTwenty = RoomAcousticsCalculator.Calculate(Arrivals(direct, (0.025, 0.5)));
        Assert.Equal(10 * Math.Log10(0.5), atTwenty.StageSupportEarlyDb[2], 9);

        // 100 ms after is late, not early; 1000 ms after is past the end
        var atHundred = RoomAcousticsCalculator.Calculate(Arrivals(direct, (0.105, 0.5)));
        Assert.True(double.IsNaN(atHundred.StageSupportEarlyDb[2]));
        Assert.Equal(10 * Math.Log10(0.5), atHundred.StageSupportLateDb[2], 9);
        var atSecond = RoomAcousticsCalculator.Calculate(Arrivals(direct, (1.005, 0.5)));
        Assert.True(double.IsNaN(atSecond.StageSupportLateDb[2]));
    }

    [Fact]
    public void ABandWithNoEnergy_HasNoSupport_WhileTheOthersAreUnaffected()
    {
        var h = new double[2000];
        h[3] = 1; h[40] = 0.2; h[300] = 0.1;
        var energy = Enumerable.Range(0, 8).Select(b => b == 5 ? new double[2000] : h).ToArray();
        var m = RoomAcousticsCalculator.Calculate(new ImpulseResponse(Dt, energy, OctaveSpectrum.Flat(1)));

        Assert.True(double.IsNaN(m.StageSupportEarlyDb[5]) && double.IsNaN(m.StageSupportLateDb[5]));
        Assert.Equal(10 * Math.Log10(0.2), m.StageSupportEarlyDb[4], 9);
    }

    [Fact]
    public void EachBandTakesItsOwnDirectSound()
    {
        var a = new double[2000]; a[3] = 1; a[40] = 0.2;
        var b = new double[2000]; b[20] = 1; b[57] = 0.4;           // its own direct sound 17 ms later, its reflection 37 ms after that
        var energy = Enumerable.Range(0, 8).Select(i => i < 4 ? a : b).ToArray();
        var m = RoomAcousticsCalculator.Calculate(new ImpulseResponse(Dt, energy, OctaveSpectrum.Flat(1)));

        Assert.Equal(10 * Math.Log10(0.2), m.StageSupportEarlyDb[0], 9);
        Assert.Equal(10 * Math.Log10(0.4), m.StageSupportEarlyDb[7], 9);
    }

    [Fact]
    public void TheTotalSupport_IsEverythingFrom20MsTo1SOverTheFirst10Ms()
    {
        // 40 ms and 300 ms are both in it; 1.2 s is not
        var m = RoomAcousticsCalculator.Calculate(Arrivals((0.003, 1.0), (0.008, 0.5), (0.040, 0.2), (0.300, 0.1), (1.2, 0.3)));
        for (var band = 0; band < 8; band++) Assert.Equal(10 * Math.Log10(0.3 / 1.5), m.StageSupportTotalDb!.Value[band], 9);
        Assert.Equal(10 * Math.Log10(0.3 / 1.5), BroadbandMetrics.From(m).StageSupportTotalDb, 9);
    }

    [Fact]
    public void TheEarlyEnsembleLevel_IsTheFirst80MsOverTheDirectSoundAt1M()
    {
        // the free-field reference at 10 m is 0.01, so the direct sound at 1 m is 1: the first 80 ms (1 + 0.5 + 0.2) over it, the arrival at 300 ms left out
        var h = new double[2000]; h[3] = 1; h[8] = 0.5; h[40] = 0.2; h[300] = 0.1;
        var m = RoomAcousticsCalculator.Calculate(new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(0.01)));
        for (var band = 0; band < 8; band++)
        {
            Assert.Equal(10 * Math.Log10(1.7), m.EarlyEnsembleLevelDb!.Value[band], 9);
            Assert.Equal(m.EarlyStrengthGDb[band] - 20, m.EarlyEnsembleLevelDb!.Value[band], 9);
        }
    }
}
