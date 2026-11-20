using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class MeasuredThirdOctaveResponseTests
{
    private const int Rate = 48000;

    private static double[] Decaying(double t60S, double lengthS, double delayS = 0.05, double floorRms = 0, int seed = 7)
    {
        var random = new Random(seed);
        var x = new double[(int)(lengthS * Rate)];
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        x[(int)(delayS * Rate)] = 8;
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)Rate - delayS;
            if (t >= 0) x[i] += Gauss() * Math.Pow(10, -3 * t / t60S);
            x[i] += floorRms * Gauss();
        }
        return x;
    }

    [Fact]
    public void TheBands_AreTheNominalOnes_UpToTheLastThatFitsUnderNyquist()
    {
        var a = MeasuredThirdOctaveResponse.Analyse(Decaying(0.8, 2.5), Rate);
        Assert.Equal(MeasuredThirdOctaveResponse.NominalCentresHz.ToArray(), a.CentresHz);   // 8 kHz band's upper edge is 8.98 kHz, under 24 kHz
        Assert.Equal(a.CentresHz.Length, a.T30S.Length);
        Assert.Equal(a.CentresHz.Length, a.DynamicRangeDb.Length);

        var slow = MeasuredThirdOctaveResponse.Analyse(Decaying(0.8, 2.5).Take((int)(2.5 * Rate)).ToArray(), 16000);   // read as 16 kHz: Nyquist 8 kHz
        Assert.True(slow.CentresHz.Max() < 8000);
        Assert.Equal(slow.CentresHz.Length, slow.EarlyDecayTimeS.Length);
    }

    [Fact]
    public void ADecayOfKnownReverberationTime_IsMeasuredBackInEveryBandFrom125HzUp()
    {
        var a = MeasuredThirdOctaveResponse.Analyse(Decaying(0.8, 2.5), Rate);

        for (var i = 0; i < a.CentresHz.Length; i++)
        {
            if (a.CentresHz[i] < 125) continue;
            Assert.InRange(a.T30S[i], 0.8 * 0.85, 0.8 * 1.15);
            Assert.InRange(a.T20S[i], 0.8 * 0.85, 0.8 * 1.15);
            // the early decay of a narrow band of random noise scatters more (it rests on 10 dB of a few cycles), so only where the band is wide enough in hertz
            if (a.CentresHz[i] >= 500) Assert.InRange(a.EarlyDecayTimeS[i], 0.8 * 0.75, 0.8 * 1.25);
        }
    }

    [Fact]
    public void TheThirdOctaveAverage_AgreesWithTheOctaveBand_ForAFlatDecay()
    {
        var x = Decaying(1.0, 3.0);
        var third = MeasuredThirdOctaveResponse.Analyse(x, Rate);
        var octave = MeasuredImpulseResponse.Analyse(x, Rate);

        foreach (var hz in new[] { 500, 1000, 2000 })
        {
            var inside = Enumerable.Range(0, third.CentresHz.Length).Where(i => third.CentresHz[i] * 1.0 / hz is >= 0.79 and <= 1.27).ToArray();   // the three third-octaves of the octave
            Assert.Equal(3, inside.Length);
            Assert.Equal(octave.Metrics.T30S.AtFrequency(hz), inside.Average(i => third.T30S[i]), 1);   // within 0.05 s
            Assert.Equal(octave.Metrics.C80Db.AtFrequency(hz), inside.Average(i => third.C80Db[i]), 0); // within 0.5 dB
        }
    }

    [Fact]
    public void ClarityAndCentreTime_FollowTheDecay_InAThirdOctaveBand()
    {
        var a = MeasuredThirdOctaveResponse.Analyse(Decaying(1.0, 3.0), Rate);
        var i = Array.IndexOf(a.CentresHz, 1000);

        var early = 1 - Math.Exp(-Math.Log(1e6) * 0.08 / 1.0);
        Assert.Equal(10 * Math.Log10(early / (1 - early)), a.C80Db[i], 0);
        Assert.InRange(a.CentreTimeS[i], 1.0 / Math.Log(1e6) * 0.9, 1.0 / Math.Log(1e6) * 1.1);
        Assert.InRange(a.D50[i], 0, 1);
    }

    [Fact]
    public void ADecayTimeNeedsItsDynamicRange_AsInTheOctaveAnalysis()
    {
        // steady noise at 40 dB under the start of a quick decay: enough for EDT and T20, not for T30
        var a = MeasuredThirdOctaveResponse.Analyse(Decaying(0.8, 2.5, floorRms: 0.01), Rate);
        var i = Array.IndexOf(a.CentresHz, 1000);

        Assert.InRange(a.DynamicRangeDb[i], 30, 50);
        Assert.True(double.IsNaN(a.T30S[i]));             // needs 45 dB
        Assert.False(double.IsNaN(a.T20S[i]));            // needs 35 dB
        Assert.False(double.IsNaN(a.EarlyDecayTimeS[i])); // needs 20 dB
        Assert.False(double.IsNaN(a.C80Db[i]));          // clarity does not need the range
    }

    [Fact]
    public void BadInput_IsRefusedAsInTheOctaveAnalysis()
    {
        Assert.Throws<ArgumentException>(() => MeasuredThirdOctaveResponse.Analyse(new double[48000], Rate));      // silence
        Assert.Throws<ArgumentException>(() => MeasuredThirdOctaveResponse.Analyse(Decaying(0.8, 2.5), 8000));      // too slow
        Assert.Throws<ArgumentException>(() => MeasuredThirdOctaveResponse.Analyse(new double[] { 0, 1, 0.5 }, Rate)); // too short
        Assert.Throws<ArgumentNullException>(() => MeasuredThirdOctaveResponse.Analyse(null!, Rate));
    }

    [Fact]
    public void TheOctaveAnalysis_IsUnchangedByTheRefactor()
    {
        var a = MeasuredImpulseResponse.Analyse(Decaying(0.8, 2.5), Rate);
        foreach (var hz in new[] { 500, 1000, 2000 })
            Assert.InRange(a.Metrics.T30S.AtFrequency(hz), 0.8 * 0.9, 0.8 * 1.1);
        Assert.Equal(0.05, a.DirectArrivalS, 3);
        Assert.Equal(2.45, a.DurationS, 3);
    }
}
