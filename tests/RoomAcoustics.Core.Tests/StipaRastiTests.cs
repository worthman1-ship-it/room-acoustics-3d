using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>STIPA and RaSTI, the quick forms of the Speech Transmission Index.</summary>
public sealed class StipaRastiTests
{
    private static readonly OctaveSpectrum Speech = new(new double[] { 60, 60, 62, 62, 58, 52, 46, 38 });
    private static readonly OctaveSpectrum Quiet = OctaveSpectrum.Flat(0);

    private static Func<int, double, double> Decay(double t60) => (_, fm) => 1 / Math.Sqrt(1 + Math.Pow(2 * Math.PI * fm * t60 / 13.8, 2));

    [Fact]
    public void WithNoReverberationOrNoise_BothAreNearOne()
    {
        Assert.InRange(SpeechTransmissionIndex.Stipa(Speech, Quiet, Decay(0)), 0.95, 1.0);
        Assert.InRange(SpeechTransmissionIndex.Rasti(Speech, Quiet, Decay(0)), 0.95, 1.0);
    }

    [Fact]
    public void LongerReverberation_LowersBoth()
    {
        double Stipa(double t) => SpeechTransmissionIndex.Stipa(Speech, Quiet, Decay(t));
        double Rasti(double t) => SpeechTransmissionIndex.Rasti(Speech, Quiet, Decay(t));
        Assert.True(Stipa(0.5) > Stipa(1.5) && Stipa(1.5) > Stipa(4));
        Assert.True(Rasti(0.5) > Rasti(1.5) && Rasti(1.5) > Rasti(4));
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.0)]
    [InlineData(2.5)]
    public void StipaStaysCloseToTheFullSti_InAnExponentialDecay(double t60)
    {
        var full = SpeechTransmissionIndex.Calculate(Speech, Quiet, Decay(t60));
        Assert.InRange(SpeechTransmissionIndex.Stipa(Speech, Quiet, Decay(t60)), full - 0.05, full + 0.05);
    }

    [Fact]
    public void RastiIsTheMeanOfTheFiveHundredHertzAndTwoKilohertzBands_AndIgnoresTheOtherBands()
    {
        // only bands 3 (500 Hz) and 5 (2 kHz) are looked at, so a hopeless modulation transfer in the rest changes nothing
        var good = SpeechTransmissionIndex.Rasti(Speech, Quiet, Decay(1));
        var badElsewhere = SpeechTransmissionIndex.Rasti(Speech, Quiet, (band, fm) => band is 3 or 5 ? Decay(1)(band, fm) : 0.01);
        Assert.Equal(good, badElsewhere);
        // and it falls when only the 2 kHz band gets worse
        Assert.True(SpeechTransmissionIndex.Rasti(Speech, Quiet, (band, fm) => band == 5 ? Decay(6)(band, fm) : Decay(1)(band, fm)) < good);
    }

    [Fact]
    public void Noise_LowersThem()
    {
        var noisy = NoiseCriterion.Curve(55);
        Assert.True(SpeechTransmissionIndex.Stipa(Speech, noisy, Decay(1)) < SpeechTransmissionIndex.Stipa(Speech, Quiet, Decay(1)));
        Assert.True(SpeechTransmissionIndex.Rasti(Speech, noisy, Decay(1)) < SpeechTransmissionIndex.Rasti(Speech, Quiet, Decay(1)));
    }

    [Fact]
    public void TheModulationTransferOfADirectSoundPlusADecay_IsOneForOnlyDirectSoundAndTheDecaysOwnForOnlyReverberation()
    {
        Assert.Equal(1.0, StatisticalEstimate.ModulationTransfer(4, 10, 0, 1.2), 9);
        Assert.Equal(Decay(1.2)(0, 4), StatisticalEstimate.ModulationTransfer(4, 0, 10, 1.2), 2); // the STI's 13.8 against the estimate's 13.82
    }
}
