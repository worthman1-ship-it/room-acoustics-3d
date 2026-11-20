using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class ResampleTests
{
    private static double[] Tone(double hz, int rate, double seconds) =>
        Enumerable.Range(0, (int)(rate * seconds)).Select(i => Math.Sin(2 * Math.PI * hz * i / rate)).ToArray();

    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(16000, 48000)]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    public void AToneBelowBothNyquistFrequencies_IsKept(int from, int to)
    {
        var resampled = Auralisation.Resample(Tone(1000, from, 0.5), from, to);
        var expected = Tone(1000, to, 0.5);

        Assert.Equal(expected.Length, resampled.Length);
        var edge = (int)(0.05 * to);   // the ends see the kernel run off the signal
        var worst = Enumerable.Range(edge, resampled.Length - 2 * edge).Max(i => Math.Abs(resampled[i] - expected[i]));
        Assert.True(worst < 0.01, $"the worst sample is {worst:0.0000} off");
    }

    [Fact]
    public void AToneAboveTheNewNyquistFrequency_IsRemovedNotFoldedBack()
    {
        // 10 kHz cannot be held at 16 kHz (Nyquist 8 kHz); without the filter it would come back as 6 kHz
        var resampled = Auralisation.Resample(Tone(10000, 48000, 0.5), 48000, 16000);

        var edge = (int)(0.05 * 16000);
        var rms = Math.Sqrt(Enumerable.Range(edge, resampled.Length - 2 * edge).Average(i => resampled[i] * resampled[i]));
        Assert.True(rms < 0.02, $"the 10 kHz tone left {rms:0.0000} rms");
    }

    [Fact]
    public void TheLengthFollowsTheRatioOfTheRates_AndTheSameRateGivesACopy()
    {
        Assert.Equal(333, Auralisation.Resample(new double[1000], 48000, 16000).Length);
        Assert.Equal(48000, Auralisation.Resample(new double[44100], 44100, 48000).Length);
        Assert.Empty(Auralisation.Resample(Array.Empty<double>(), 44100, 48000));

        var same = new[] { 1.0, 2.0, 3.0 };
        var copy = Auralisation.Resample(same, 48000, 48000);
        Assert.Equal(same, copy);
        Assert.NotSame(same, copy);
    }

    [Fact]
    public void ARateThatIsNotPositive_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Auralisation.Resample(new double[10], 0, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Auralisation.Resample(new double[10], 48000, -1));
        Assert.Throws<ArgumentNullException>(() => Auralisation.Resample(null!, 48000, 16000));
    }
}

public class MeasuredAuralisationTests
{
    private static double[] Click(int length)
    {
        var x = new double[length];
        x[0] = 1;
        return x;
    }

    [Fact]
    public void AClickThroughAMeasuredRoom_ComesOutAsThatRoom_AtTheRequestedPeak()
    {
        var impulse = new double[200];
        impulse[100] = 1;
        impulse[101] = 0.5;
        impulse[103] = -0.25;

        var result = RoomAuralisation.RenderMeasured(Click(50), 48000, impulse, 48000);

        Assert.Equal(48000, result.SampleRateHz);
        Assert.Equal(0.5, result.Left[0], 12);          // the direct sound at the peak level, with the lead-in gone
        Assert.Equal(0.25, result.Left[1], 12);
        Assert.Equal(0.0, result.Left[2], 12);
        Assert.Equal(-0.125, result.Left[3], 12);
        Assert.Equal(result.Left, result.Right);
        Assert.NotSame(result.Left, result.Right);
        Assert.Equal(0, result.InterauralLevelDifferenceDb);
        Assert.Equal(Vec3.Zero, result.LeftEar);
    }

    [Fact]
    public void WhatCameBeforeTheDirectSound_DoesNotDelayTheResult()
    {
        var bare = new double[100];
        bare[0] = 1;
        bare[10] = 0.4;
        var delayed = new double[50].Concat(bare).ToArray();
        var dry = Enumerable.Range(0, 400).Select(i => Math.Sin(i * 0.3)).ToArray();

        var a = RoomAuralisation.RenderMeasured(dry, 48000, bare, 48000);
        var b = RoomAuralisation.RenderMeasured(dry, 48000, delayed, 48000);

        Assert.Equal(a.Left.Length, b.Left.Length);
        for (var i = 0; i < a.Left.Length; i++) Assert.Equal(a.Left[i], b.Left[i], 12);
    }

    [Fact]
    public void AResponseAtAnotherRate_IsBroughtToTheSoundsRate()
    {
        var impulse = new double[2400];                 // 0.1 s at 24 kHz
        impulse[0] = 1;
        var dry = Click(4800);                           // 0.1 s at 48 kHz

        var result = RoomAuralisation.RenderMeasured(dry, 48000, impulse, 24000);

        Assert.Equal(48000, result.SampleRateHz);
        Assert.Equal(dry.Length + 4800 - 1, result.Left.Length);   // the response is now 0.1 s of 48 kHz samples
        Assert.Equal(0.5, result.Left.Max(Math.Abs), 12);
    }

    [Fact]
    public void TheDecayTimeOfTheRoomSurvivesAChangeOfSampleRate()
    {
        // a decaying noise response at 24 kHz, heard through a 48 kHz click, should still decay in the same time
        var random = new Random(5);
        var impulse = new double[(int)(2.5 * 24000)];
        impulse[0] = 8;
        for (var i = 1; i < impulse.Length; i++)
        {
            var gauss = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            impulse[i] = gauss * Math.Pow(10, -3.0 * i / 24000 / 0.7);
        }
        var direct = MeasuredImpulseResponse.Analyse(impulse, 24000);

        var heard = RoomAuralisation.RenderMeasured(Click(100), 48000, impulse, 24000);
        var through = MeasuredImpulseResponse.Analyse(heard.Left, 48000);

        foreach (var hz in new[] { 500, 1000, 2000 })
            Assert.InRange(through.Metrics.T30S.AtFrequency(hz) / direct.Metrics.T30S.AtFrequency(hz), 0.95, 1.05);
        Assert.InRange(through.Metrics.T30S.AtFrequency(1000), 0.7 * 0.9, 0.7 * 1.1);
    }

    [Fact]
    public void WhatCannotBeRendered_IsRefusedWithAReason()
    {
        var impulse = new double[100];
        impulse[0] = 1;

        Assert.Throws<ArgumentException>(() => RoomAuralisation.RenderMeasured(Array.Empty<double>(), 48000, impulse, 48000));
        var silent = Assert.Throws<ArgumentException>(() => RoomAuralisation.RenderMeasured(Click(10), 48000, new double[100], 48000));
        Assert.Contains("no sound", silent.Message);
        var tooLong = new double[(int)(21 * 8000)];
        tooLong[0] = 1;
        Assert.Contains("20 s", Assert.Throws<ArgumentException>(() => RoomAuralisation.RenderMeasured(Click(10), 8000, tooLong, 8000)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomAuralisation.RenderMeasured(Click(10), 4000, impulse, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomAuralisation.RenderMeasured(Click(10), 48000, impulse, 48000, peak: 0));
        Assert.Throws<ArgumentNullException>(() => RoomAuralisation.RenderMeasured(null!, 48000, impulse, 48000));
    }
}
