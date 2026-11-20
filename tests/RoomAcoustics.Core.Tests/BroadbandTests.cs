using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The ISO 3382-1 single-number (broadband) values, and the Speech Transmission Index at a receiver.</summary>
public class BroadbandTests
{
    private static List<Surface> Room(double alpha)
    {
        var material = new AbsorptionMaterial("flat", "test", Enumerable.Repeat<double?>(alpha, 8).ToArray());
        return new List<Surface> { new("floor", 60, material), new("ceiling", 60, material), new("walls", 96, material) };
    }
    private const double Volume = 180;
    private static readonly ReverberationOptions NoAir = new(ReverberationMethod.EyringNorris, false);

    [Fact]
    public void AMeanOfBandsLeavesOutTheBandsThatCannotBeWorkedOut()
    {
        var spectrum = new OctaveSpectrum(1, 2, 3, 4, double.NaN, 6, 7, 8);
        Assert.Equal(4, BroadbandMetrics.Mean(spectrum, 3, 4), 12);        // 500 and 1000 Hz: only the 500 Hz value
        Assert.Equal(5, BroadbandMetrics.Mean(spectrum, 3, 4, 5), 12);     // 500, 1000 and 2000 Hz
        Assert.True(double.IsNaN(BroadbandMetrics.Mean(new OctaveSpectrum(double.NaN, 1, 1, 1, 1, 1, 1, 1), 0)));
    }

    [Fact]
    public void LJIsAveragedAsEnergy_AsIso3382Says()
    {
        // ISO 3382-1 Equation A.17: 10 lg (0.25 sum 10^(LJ/10)) over 125 to 1000 Hz; -10 and -20 dB average to -12.6 dB, not -15
        var lj = new OctaveSpectrum(double.NaN, -10, -10, -20, -20, 0, 0, 0);
        Assert.Equal(10 * Math.Log10(0.25 * (2 * 0.1 + 2 * 0.01)), BroadbandMetrics.EnergyMean(lj, 1, 2, 3, 4), 12);
        Assert.Equal(-12.596, BroadbandMetrics.EnergyMean(lj, 1, 2, 3, 4), 3);
    }

    [Fact]
    public void TheSingleNumbersAreTheIsoBandAverages()
    {
        var estimate = StatisticalEstimate.Calculate(Volume, Room(0.3), 6, NoAir);
        var broadband = BroadbandMetrics.From(estimate);
        // reverberation, centre time, D50 and strength G at 500 and 1000 Hz
        Assert.Equal((estimate.EyringT60S[3] + estimate.EyringT60S[4]) / 2, broadband.T30S, 12);
        Assert.Equal(broadband.T30S, broadband.EdtS, 12);
        Assert.Equal((estimate.D50[3] + estimate.D50[4]) / 2, broadband.D50, 12);
        Assert.Equal((estimate.CentreTimeS[3] + estimate.CentreTimeS[4]) / 2, broadband.CentreTimeS, 12);
        Assert.Equal((estimate.StrengthGDb[3] + estimate.StrengthGDb[4]) / 2, broadband.StrengthGDb, 12);
        // clarity C80 and C50 at 500 and 1000 Hz (ISO 3382-1:2009 Table A.1)
        Assert.Equal((estimate.C80Db[3] + estimate.C80Db[4]) / 2, broadband.C80Db, 12);
        Assert.Equal((estimate.C50Db[3] + estimate.C50Db[4]) / 2, broadband.C50Db, 12);
        // the lateral measures need arrival directions, which a statistical estimate has none of
        Assert.True(double.IsNaN(broadband.LateralFraction80));
        Assert.True(double.IsNaN(broadband.LateLateralLevelDb));
    }

    [Fact]
    public void TheEarlyAndLateStrengthAddUpToTheStrength()
    {
        var estimate = StatisticalEstimate.Calculate(Volume, Room(0.3), 6, NoAir);
        for (var b = 0; b < 8; b++)
        {
            var sum = Math.Pow(10, estimate.EarlyStrengthGDb[b] / 10) + Math.Pow(10, estimate.LateStrengthGDb[b] / 10);
            Assert.Equal(Math.Pow(10, estimate.StrengthGDb[b] / 10), sum, 6);
            // and their difference is the clarity C80
            Assert.Equal(estimate.C80Db[b], estimate.EarlyStrengthGDb[b] - estimate.LateStrengthGDb[b], 6);
        }
    }

    [Fact]
    public void FarFromTheSource_TheModulationTransferIsThatOfAnExponentialDecay()
    {
        var far = StatisticalEstimate.Calculate(Volume, Room(0.2), 1000, NoAir);
        for (var b = 0; b < 8; b++)
            for (var i = 0; i < SpeechTransmissionIndex.Modulations.Count; i++)
            {
                var fm = SpeechTransmissionIndex.Modulations[i];
                Assert.Equal(1 / Math.Sqrt(1 + Math.Pow(2 * Math.PI * fm * far.EyringT60S[b] / 13.82, 2)), far.ModulationTransfer[b][i], 3);
            }
    }

    [Fact]
    public void CloseToTheSource_TheDirectSoundKeepsTheModulationFull()
    {
        var near = StatisticalEstimate.Calculate(Volume, Room(0.2), 0.1, NoAir);
        Assert.All(near.ModulationTransfer.SelectMany(row => row), m => Assert.InRange(m, 0.97, 1.0));
    }

    private static double Sti(double alpha, double distance, int nc = 30, double speechDbA = 60, SpeechWeighting voice = SpeechWeighting.Male)
    {
        var estimate = StatisticalEstimate.Calculate(Volume, Room(alpha), distance, NoAir);
        return SpeechTransmissionIndex.AtReceiver(estimate.StrengthGDb, (band, fm) => estimate.ModulationTransfer[band][SpeechTransmissionIndex.Modulations.ToList().IndexOf(fm)], speechDbA, nc, voice);
    }

    [Fact]
    public void TheIndexFallsWithReverberation_WithDistance_AndWithBackgroundNoise_AndRisesWithTheSpeechLevel()
    {
        Assert.InRange(Sti(0.3, 4), 0, 1);
        Assert.True(Sti(0.6, 4) > Sti(0.1, 4), "a deader room is more intelligible");
        Assert.True(Sti(0.3, 2) > Sti(0.3, 12), "nearer is more intelligible");
        Assert.True(Sti(0.3, 4, nc: 25) > Sti(0.3, 4, nc: 55), "quieter is more intelligible");
        Assert.True(Sti(0.3, 4, nc: 50, speechDbA: 70) > Sti(0.3, 4, nc: 50, speechDbA: 55), "louder speech is more intelligible in noise");
    }

    [Fact]
    public void ADeadQuietRoomNearTheTalkerIsExcellent_AndAReverberantNoisyOneFarAwayIsBad()
    {
        Assert.True(Sti(0.7, 1.5, nc: 20, speechDbA: 65) > 0.75);
        Assert.True(Sti(0.03, 15, nc: 60, speechDbA: 55) < 0.45);
    }

    [Fact]
    public void ThePredictorAndTheReceiverAgree_WhenTheRoomIsJustAnExponentialDecayWithTheSpeechAtTheListener()
    {
        // 61 dBA at the listener is the predictor's reference; put the same spectrum there through a G that makes up for the 1 m to listener distance
        var rt = OctaveSpectrum.Flat(0.8);
        var speech = SpeechTransmissionIndex.PredictorSpeech(61);
        var noise = NoiseCriterion.Curve(30);
        var viaReverberationTimes = SpeechTransmissionIndex.Calculate(speech, noise, rt);
        var strength = OctaveSpectrum.Flat(20); // speech at the listener = speech at 1 m - 20 dB + G = speech at 1 m
        var viaReceiver = SpeechTransmissionIndex.AtReceiver(strength, (band, fm) => 1 / Math.Sqrt(1 + Math.Pow(2 * Math.PI * fm * rt[band] / 13.8, 2)), 61, 30, SpeechWeighting.Male);
        Assert.Equal(viaReverberationTimes, viaReceiver, 2);
    }

    [Fact]
    public void BassAndBrillianceRatios_AreBeranekSRatiosOfTheReverberationTimes()
    {
        var t = new OctaveSpectrum(2.0, 1.8, 1.6, 1.2, 1.0, 0.9, 0.7, 0.5);
        Assert.Equal((1.8 + 1.6) / (1.2 + 1.0), BroadbandMetrics.BassRatioOf(t), 12);
        Assert.Equal((0.9 + 0.7) / (1.2 + 1.0), BroadbandMetrics.BrillianceOf(t), 12);
    }

    [Fact]
    public void TheEchoCriterionOfAWholeRoom_IsTheWorstOfTheBandsFrom500To2000Hz()
    {
        var ek = new OctaveSpectrum(9, 9, 9, 0.2, 0.7, 0.4, 9, 9);   // the bands outside 500 to 2000 Hz are not looked at
        Assert.Equal(0.7, BroadbandMetrics.Worst(ek, 3, 4, 5), 12);
        Assert.True(double.IsNaN(BroadbandMetrics.Worst(new OctaveSpectrum(double.NaN, 1, 1, double.NaN, double.NaN, double.NaN, 1, 1), 3, 4, 5)));
    }

    [Fact]
    public void AlconsFallsAsTheIndexRises_AndIsHeldTo0To100Percent()
    {
        Assert.Equal(170.5405 * Math.Exp(-5.419 * 0.5), SpeechTransmissionIndex.Alcons(0.5), 6);
        Assert.Equal(100, SpeechTransmissionIndex.Alcons(0), 12);
        Assert.True(SpeechTransmissionIndex.Alcons(0.7) < SpeechTransmissionIndex.Alcons(0.4));
        Assert.True(double.IsNaN(SpeechTransmissionIndex.Alcons(double.NaN)));
    }
}
