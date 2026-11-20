using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class StatisticalEstimateTests
{
    // a 10 x 6 x 3 m room with the same coefficient everywhere
    private static List<Surface> Room(double alpha)
    {
        var material = new AbsorptionMaterial("flat", "test", Enumerable.Repeat<double?>(alpha, 8).ToArray());
        return new List<Surface> { new("floor", 60, material), new("ceiling", 60, material), new("walls", 96, material) };
    }
    private const double Volume = 180;
    private static readonly ReverberationOptions NoAir = new(ReverberationMethod.EyringNorris, false);

    [Fact]
    public void TheReverberationTimesAreSabineAndEyring_AndEyringIsTheShorter()
    {
        var estimate = StatisticalEstimate.Calculate(Volume, Room(0.5), 5, NoAir);
        var sabine = ReverberationCalculator.Calculate(Volume, Room(0.5), new ReverberationOptions(ReverberationMethod.Sabine, false));
        for (var b = 0; b < 8; b++)
        {
            Assert.Equal(sabine.T60[b], estimate.SabineT60S[b], 9);
            Assert.True(estimate.EyringT60S[b] < estimate.SabineT60S[b]);
        }
    }

    [Fact]
    public void Close_ToTheSource_AllTheEnergyIsDirect()
    {
        var estimate = StatisticalEstimate.Calculate(Volume, Room(0.2), 0.1, NoAir);
        Assert.All(estimate.D50.Bands, d => Assert.True(d > 0.99));
        Assert.All(estimate.CentreTimeS.Bands, ts => Assert.True(ts < 0.001));
    }

    [Fact]
    public void AtTheCriticalDistance_DirectAndReverberantEnergyAreEqual()
    {
        var room = Room(0.2);
        var dc = StatisticalEstimate.Calculate(Volume, room, 5, NoAir).CriticalDistanceM;
        for (var b = 0; b < 8; b++)
        {
            var atDc = StatisticalEstimate.Calculate(Volume, room, dc[b], NoAir);
            var t = atDc.EyringT60S[b];
            var earlyOfReverberant = 1 - Math.Exp(-13.82 * 0.05 / t);
            // half the energy is direct, and the early part of the other half is what the 50 ms window takes
            Assert.Equal(0.5 + 0.5 * earlyOfReverberant, atDc.D50[b], 6);
            // and the level is that of two equal energies, 3 dB above either
            var reverberant = 31200 * t / Volume;
            Assert.Equal(10 * Math.Log10(2 * reverberant), atDc.StrengthGDb[b], 6);
        }
    }

    [Fact]
    public void FarFromTheSource_TheEarlyShareIsThatOfTheDiffuseDecay()
    {
        var far = StatisticalEstimate.Calculate(Volume, Room(0.2), 1000, NoAir);
        for (var b = 0; b < 8; b++)
        {
            var t = far.EyringT60S[b];
            Assert.Equal(1 - Math.Exp(-13.82 * 0.05 / t), far.D50[b], 3);
            Assert.Equal(10 * Math.Log10((1 - Math.Exp(-13.82 * 0.08 / t)) / Math.Exp(-13.82 * 0.08 / t)), far.C80Db[b], 2);
            Assert.Equal(t / 13.82, far.CentreTimeS[b], 3);
        }
    }

    [Fact]
    public void MoreAbsorption_GivesShorterTimes_ClearerSound_AndALongerCriticalDistance()
    {
        var live = StatisticalEstimate.Calculate(Volume, Room(0.1), 8, NoAir);
        var dead = StatisticalEstimate.Calculate(Volume, Room(0.6), 8, NoAir);
        for (var b = 0; b < 8; b++)
        {
            Assert.True(dead.EyringT60S[b] < live.EyringT60S[b]);
            Assert.True(dead.C80Db[b] > live.C80Db[b]);
            Assert.True(dead.StrengthGDb[b] < live.StrengthGDb[b]);
            Assert.True(dead.CriticalDistanceM[b] > live.CriticalDistanceM[b]);
        }
    }

    [Fact]
    public void ADirectionalSource_ReachesFurther_AndTheCriticalDistanceGrowsWithTheSquareRootOfQ()
    {
        var omni = StatisticalEstimate.Calculate(Volume, Room(0.2), 5, NoAir, 1);
        var horn = StatisticalEstimate.Calculate(Volume, Room(0.2), 5, NoAir, 4);
        for (var b = 0; b < 8; b++) Assert.Equal(2 * omni.CriticalDistanceM[b], horn.CriticalDistanceM[b], 9);
    }
}
