using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class SpeechIntelligibilityIndexTests
{
    [Fact]
    public void TheStandardsWorkedExample_GivesItsIndex()
    {
        // ANSI S3.5-1997 annex C, table C.1: the octave band procedure, SII = 0.504
        var sii = SpeechIntelligibilityIndex.Calculate(new double[] { 50, 40, 40, 30, 20, 0 }, new double[] { 70, 65, 45, 25, 1, -15 }, new double[6]);
        Assert.Equal(0.504, sii, 3);
    }

    [Fact]
    public void NormalSpeechInSilence_IsWhollyIntelligible_AndNoiseOverItIsNot()
    {
        // the standard speech spectrum at normal vocal effort, with no noise (-50 dB, as the standard takes it) and with noise 15 dB over it; in silence
        // only the 8 kHz band falls short, where normal speech (1.13 dB) is only 8.23 dB over the ear's internal noise (-7.1 dB)
        double[] normal = { 34.75, 34.27, 25.01, 17.32, 9.33, 1.13 };
        Assert.Equal(1 - 0.0549 * (1 - (1.13 + 7.1 + 15) / 30), SpeechIntelligibilityIndex.Calculate(normal, Enumerable.Repeat(-50.0, 6).ToArray()), 9);
        Assert.Equal(0, SpeechIntelligibilityIndex.Calculate(normal, normal.Select(e => e + 15).ToArray()), 9);
        // noise as loud as the speech: half way in each band
        Assert.Equal(0.5, SpeechIntelligibilityIndex.Calculate(normal, normal), 9);
    }

    [Fact]
    public void VeryLoudSpeech_LosesALittle()
    {
        double[] normal = { 34.75, 34.27, 25.01, 17.32, 9.33, 1.13 };
        var shouted = SpeechIntelligibilityIndex.Calculate(normal.Select(e => e + 40).ToArray(), Enumerable.Repeat(-50.0, 6).ToArray());
        Assert.Equal(1 - 30.0 / 160, shouted, 9); // the level distortion factor, 1 - (E' - U - 10) / 160
    }

    [Fact]
    public void InARoom_ReverberationAndNoise_EachLowerIt()
    {
        // the standard speech spectrum 10 dB over normal effort, as octave band levels at the listener
        double[] normal = { 34.75, 34.27, 25.01, 17.32, 9.33, 1.13 };
        var speech = new OctaveSpectrum(new[] { 0.0, 0 }.Concat(normal.Select((u, i) => u + SpeechIntelligibilityIndex.BandwidthDb(i) + 10)).ToArray());
        var quiet = OctaveSpectrum.Flat(-100);
        double Exponential(double t, double fm) => 1 / Math.Sqrt(1 + Math.Pow(2 * Math.PI * fm * t / 13.8, 2));

        var dry = SpeechIntelligibilityIndex.InRoom(speech, quiet, (_, _) => 1);
        var live = SpeechIntelligibilityIndex.InRoom(speech, quiet, (_, fm) => Exponential(1, fm));
        var veryLive = SpeechIntelligibilityIndex.InRoom(speech, quiet, (_, fm) => Exponential(3, fm));
        var noisy = SpeechIntelligibilityIndex.InRoom(speech, new OctaveSpectrum(speech.Bands.Select(l => l - 5).ToArray()), (_, _) => 1);

        Assert.Equal(1, dry, 6);
        Assert.True(live < dry && veryLive < live, $"{dry} {live} {veryLive}");
        // noise 5 dB under the speech: the apparent ratio is 5 dB in each band, so each band's audibility is (5 + 15) / 30
        Assert.Equal(20.0 / 30, noisy, 6);
    }

    [Fact]
    public void TheEquivalentLevels_ShareOutTheSpeechAndNoiseByTheApparentRatio()
    {
        // with no reverberation, the equivalent speech and noise are the speech and noise themselves: SII from levels equals SII in the room
        var speech = new OctaveSpectrum(new double[] { 0, 0, 62, 58, 55, 50, 44, 38 });
        var noise = new OctaveSpectrum(new double[] { 0, 0, 55, 50, 45, 40, 35, 30 });
        var spectrum = (OctaveSpectrum s) => Enumerable.Range(0, 6).Select(i => s[i + 2] - SpeechIntelligibilityIndex.BandwidthDb(i)).ToArray();
        Assert.Equal(SpeechIntelligibilityIndex.Calculate(spectrum(speech), spectrum(noise)), SpeechIntelligibilityIndex.InRoom(speech, noise, (_, _) => 1), 6);
    }
}
