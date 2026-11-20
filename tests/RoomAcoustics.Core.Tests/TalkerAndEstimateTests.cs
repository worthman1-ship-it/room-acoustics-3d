using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The human-talker pattern, and the statistical estimate given the direct sound's energy band by band (for several sources).</summary>
public sealed class TalkerAndEstimateTests
{
    [Fact]
    public void ATalkerHasTheMeasuredDirectivityIndexOnAxis_CloseToThePublishedOne_AndIsQuieterBehind()
    {
        var front = new Vec3(1, 0, 0);
        var talker = BalloonDirectivity.Talker().Aimed(front);
        // the measured balloon's index (worked out independently from the report's table), and ODEON's from 250 Hz up, which the balloon is within
        // 0.4 dB of to 2 kHz and about 1 dB under above, since nothing was measured more than 53 degrees below the horizontal
        double[] measured = { 1.47, 1.47, 2.62, 1.53, 1.39, 3.91, 4.01, 4.07 };
        double?[] published = { null, null, 2.9, 1.8, 1.5, 4.0, 5.0, 5.1 };
        for (var b = 0; b < 8; b++)
        {
            var band = talker.ForBand(b);
            // the pattern is normalised to an average of 1 over the sphere, so the gain on axis is the directivity factor Q
            var di = 10 * Math.Log10(band.Gain(front));
            Assert.Equal(measured[b], di, 0.1);
            if (published[b] is { } p) Assert.InRange(di, p - 1.2, p + 0.1);
            Assert.True(band.Gain(new Vec3(-1, 0, 0)) < band.Gain(new Vec3(0, 1, 0)), $"band {b}: quieter behind than to the side");
            Assert.True(band.Gain(new Vec3(0, 1, 0)) < band.Gain(front), $"band {b}: quieter to the side than in front");
        }
    }

    private static readonly ReverberationOptions NoAir = new(ReverberationMethod.EyringNorris, false);

    private static List<Surface> Room()
    {
        var material = new AbsorptionMaterial("flat", "test", Enumerable.Repeat<double?>(0.2, 8).ToArray());
        return new List<Surface> { new("floor", 60, material), new("ceiling", 60, material), new("walls", 96, material) };
    }

    [Fact]
    public void GivenTheDirectEnergy_TheEstimateIsTheOneForADistance()
    {
        var byDistance = StatisticalEstimate.Calculate(180, Room(), 4, NoAir, 2);
        var direct = Enumerable.Repeat(100 * 2 / 16.0, 8).ToArray();
        var byEnergy = StatisticalEstimate.Calculate(180, Room(), direct, Enumerable.Repeat(2.0, 8).ToArray(), NoAir);
        for (var b = 0; b < 8; b++)
        {
            Assert.Equal(byDistance.StrengthGDb[b], byEnergy.StrengthGDb[b], 12);
            Assert.Equal(byDistance.C80Db[b], byEnergy.C80Db[b], 12);
            Assert.Equal(byDistance.CriticalDistanceM[b], byEnergy.CriticalDistanceM[b], 12);
        }
    }

    [Fact]
    public void TwoSourcesSharingThePower_GiveTheEnergyMeanOfTheirDirectSounds()
    {
        // two equal sources at 2 m and 8 m: each has half the power, so the direct energy is half of 100/4 plus half of 100/64
        var direct = Enumerable.Repeat(0.5 * 100 / 4 + 0.5 * 100 / 64, 8).ToArray();
        var two = StatisticalEstimate.Calculate(180, Room(), direct, Enumerable.Repeat(1.0, 8).ToArray(), NoAir);
        var near = StatisticalEstimate.Calculate(180, Room(), 2, NoAir);
        var far = StatisticalEstimate.Calculate(180, Room(), 8, NoAir);
        for (var b = 0; b < 8; b++)
        {
            Assert.True(two.StrengthGDb[b] < near.StrengthGDb[b] && two.StrengthGDb[b] > far.StrengthGDb[b]);
            Assert.True(two.C80Db[b] < near.C80Db[b] && two.C80Db[b] > far.C80Db[b]);
        }
        Assert.Throws<ArgumentException>(() => StatisticalEstimate.Calculate(180, Room(), new double[7], Enumerable.Repeat(1.0, 8).ToArray(), NoAir));
    }
}
