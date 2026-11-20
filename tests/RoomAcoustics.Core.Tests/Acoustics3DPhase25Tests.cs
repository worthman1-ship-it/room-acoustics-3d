using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class MeasuredImpulseResponseTests
{
    private const int Rate = 48000;

    /// <summary>
    /// A direct sound (a single sample), then white noise under an exponential envelope that falls 60 dB in <paramref name="t60S"/>, after
    /// <paramref name="delayS"/> of silence, with steady noise under all of it.
    /// </summary>
    private static double[] Decaying(double t60S, double lengthS, double delayS = 0.05, double floorRms = 0, int seed = 7)
    {
        var random = new Random(seed);
        var x = new double[(int)(lengthS * Rate)];
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        x[(int)(delayS * Rate)] = 8;   // stands clear of the noise, as a real direct sound does
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)Rate - delayS;
            if (t >= 0) x[i] += Gauss() * Math.Pow(10, -3 * t / t60S);
            x[i] += floorRms * Gauss();
        }
        return x;
    }

    [Fact]
    public void ADecayOfKnownReverberationTime_IsMeasuredBackInTheMiddleBands()
    {
        var analysis = MeasuredImpulseResponse.Analyse(Decaying(0.8, 2.5), Rate);

        foreach (var hz in new[] { 500, 1000, 2000 })
        {
            Assert.InRange(analysis.Metrics.T30S.AtFrequency(hz), 0.8 * 0.9, 0.8 * 1.1);
            Assert.InRange(analysis.Metrics.T20S.AtFrequency(hz), 0.8 * 0.9, 0.8 * 1.1);
            Assert.InRange(analysis.Metrics.EarlyDecayTimeS.AtFrequency(hz), 0.8 * 0.85, 0.8 * 1.15);
        }
        Assert.Equal(Rate, analysis.SampleRateHz);
    }

    [Fact]
    public void TimeZeroIsTheDirectSound_SoTheSilenceBeforeItDoesNotCount()
    {
        var response = Decaying(0.8, 2.5, delayS: 0.0);
        var delayed = new double[(int)(0.3 * Rate)].Concat(response).ToArray();   // the same response, heard 0.3 s into the recording

        var early = MeasuredImpulseResponse.Analyse(response, Rate);
        var late = MeasuredImpulseResponse.Analyse(delayed, Rate);

        Assert.Equal(0.0, early.DirectArrivalS, 9);
        Assert.Equal(0.3, late.DirectArrivalS, 9);
        Assert.Equal(early.DurationS, late.DurationS, 9);
        for (var b = 0; b < 8; b++)
        {
            Assert.Equal(early.Metrics.T30S[b], late.Metrics.T30S[b], 9);
            Assert.Equal(early.Metrics.C80Db[b], late.Metrics.C80Db[b], 9);
        }
    }

    [Fact]
    public void ClarityAndCentreTime_FollowTheDecay()
    {
        // for exponential energy e^(-13.8 t / T), the share before 80 ms is 1 - e^(-13.8 * 0.08 / T) and the centre time is T / 13.8
        var analysis = MeasuredImpulseResponse.Analyse(Decaying(1.0, 3.0), Rate);

        var early = 1 - Math.Exp(-Math.Log(1e6) * 0.08 / 1.0);
        var expectedC80 = 10 * Math.Log10(early / (1 - early));
        Assert.Equal(expectedC80, analysis.Metrics.C80Db.AtFrequency(1000), 0);   // within 0.5 dB
        Assert.InRange(analysis.Metrics.CentreTimeS.AtFrequency(1000), 1.0 / Math.Log(1e6) * 0.9, 1.0 / Math.Log(1e6) * 1.1);
    }

    [Fact]
    public void WhatARecordingCannotHave_IsNotMadeUp()
    {
        var m = MeasuredImpulseResponse.Analyse(Decaying(0.8, 2.5), Rate).Metrics;

        Assert.True(double.IsNaN(m.StrengthGDb.AtFrequency(1000)));
        Assert.True(double.IsNaN(m.EarlyStrengthGDb.AtFrequency(1000)));
        Assert.True(double.IsNaN(m.LateralFraction80.AtFrequency(1000)));
        Assert.True(double.IsNaN(m.LateLateralLevelLJDb.AtFrequency(1000)));
    }

    [Fact]
    public void ADecayTimeNeedsItsRangeAndAMarginOfDynamicRange_AndLosesItWhenNoiseTakesOver()
    {
        // the noise floor is raised in steps: the dynamic range falls, and the decay times drop out longest first
        double[] floors = { 0, 1e-4, 1e-3, 1e-2, 1e-1 };
        var needs = new Dictionary<string, double> { ["EDT"] = 20, ["T20"] = 35, ["T30"] = 45 };
        var seenAGap = false;
        foreach (var floor in floors)
        {
            var a = MeasuredImpulseResponse.Analyse(Decaying(0.8, 2.5, floorRms: floor), Rate);
            for (var b = 0; b < 8; b++)
            {
                var range = a.DynamicRangeDb[b];
                Assert.Equal(range >= needs["EDT"], double.IsFinite(a.Metrics.EarlyDecayTimeS[b]));
                Assert.Equal(range >= needs["T20"], double.IsFinite(a.Metrics.T20S[b]));
                Assert.Equal(range >= needs["T30"], double.IsFinite(a.Metrics.T30S[b]));
                if (range < needs["T30"]) seenAGap = true;
            }
        }
        Assert.True(seenAGap, "the noisiest floor should have taken some decay time out");
    }

    [Fact]
    public void TheNoiseFloorIsTakenOff_SoASteadyHissDoesNotLengthenTheDecay()
    {
        // a floor 55 dB under the 1 kHz peak or so: left in, it would drag the 30 dB fit out
        var clean = MeasuredImpulseResponse.Analyse(Decaying(0.6, 3.0), Rate);
        var hissy = MeasuredImpulseResponse.Analyse(Decaying(0.6, 3.0, floorRms: 1e-3), Rate);

        Assert.InRange(hissy.DynamicRangeDb[4], 45, 80);
        Assert.InRange(hissy.Metrics.T30S.AtFrequency(1000), 0.6 * 0.9, 0.6 * 1.1);
        Assert.InRange(hissy.Metrics.T30S.AtFrequency(1000) / clean.Metrics.T30S.AtFrequency(1000), 0.93, 1.07);
    }

    [Fact]
    public void ABandThatIsOnlyNoise_GivesNothing()
    {
        // all noise, no decay: the end is as loud as the start
        var random = new Random(3);
        var noise = Enumerable.Range(0, Rate * 2).Select(_ => random.NextDouble() - 0.5).ToArray();

        var a = MeasuredImpulseResponse.Analyse(noise, Rate);

        for (var b = 0; b < 8; b++)
        {
            Assert.True(a.DynamicRangeDb[b] < 10);
            Assert.True(double.IsNaN(a.Metrics.T30S[b]));
            Assert.True(double.IsNaN(a.Metrics.C80Db[b]));
        }
    }

    [Fact]
    public void WhatCannotBeAnalysed_IsRefusedWithAReason()
    {
        var silent = Assert.Throws<ArgumentException>(() => MeasuredImpulseResponse.Analyse(new double[Rate], Rate));
        Assert.Contains("no sound", silent.Message);

        var slow = Assert.Throws<ArgumentException>(() => MeasuredImpulseResponse.Analyse(Decaying(0.5, 1.0, delayS: 0), 8000));
        Assert.Contains("too low", slow.Message);

        var shortOne = new double[Rate / 10];
        shortOne[0] = 1;
        var tooShort = Assert.Throws<ArgumentException>(() => MeasuredImpulseResponse.Analyse(shortOne, Rate));
        Assert.Contains("at least", tooShort.Message);

        Assert.Throws<ArgumentNullException>(() => MeasuredImpulseResponse.Analyse(null!, Rate));
    }

    [Fact]
    public void ALongRecording_IsReadOnlyAsFarAsTheLimit()
    {
        var x = new double[(int)(25 * 16000)];
        x[0] = 1;
        for (var i = 1; i < x.Length; i++) x[i] = 1e-3 * Math.Pow(10, -3.0 * i / 16000 / 2) * ((i * 2654435761L) % 1000 / 500.0 - 1);

        var a = MeasuredImpulseResponse.Analyse(x, 16000);

        Assert.Equal(MeasuredImpulseResponse.MaxDurationS, a.DurationS, 6);
    }
}
