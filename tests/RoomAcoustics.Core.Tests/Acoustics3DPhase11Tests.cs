using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class DrySignalsTests
{
    private const int Rate = 22050;

    [Fact]
    public void AClickTrain_HasOneFullScaleSamplePerInterval_StartingAtOneTenthOfASecond()
    {
        var signal = DrySignals.ClickTrain(Rate, 3.5, 1.0);

        Assert.Equal((int)Math.Round(3.5 * Rate), signal.Length);
        var clicks = Enumerable.Range(0, signal.Length).Where(i => signal[i] != 0).ToArray();
        Assert.Equal(new[] { 0.1, 1.1, 2.1, 3.1 }.Select(t => (int)Math.Round(t * Rate)), clicks);
        Assert.All(clicks, i => Assert.Equal(1, signal[i]));
    }

    [Fact]
    public void NoiseBursts_AreSilentBetweenBursts_AndPeakAtFullScale()
    {
        var signal = DrySignals.NoiseBursts(Rate, 5, onS: 0.4, periodS: 2.0);

        Assert.Equal(1, signal.Max(Math.Abs), 9);
        // Before the first burst (0.1 s), and in the gap from 0.5 s to 2.1 s.
        Assert.All(signal.Take((int)(0.099 * Rate)), x => Assert.Equal(0, x));
        Assert.All(signal.Skip((int)(0.51 * Rate)).Take((int)(1.5 * Rate)), x => Assert.Equal(0, x));
        // The second burst, from 2.1 s.
        Assert.Contains(signal.Skip((int)(2.15 * Rate)).Take((int)(0.3 * Rate)), x => Math.Abs(x) > 0.05);
    }

    [Fact]
    public void NoiseBursts_AreRampedSoTheyDoNotClick()
    {
        var signal = DrySignals.NoiseBursts(Rate, 1, onS: 0.4, periodS: 1.0);

        var start = (int)Math.Round(0.1 * Rate);
        Assert.True(Math.Abs(signal[start]) < 0.05, $"the first sample of a burst is {signal[start]}");
        Assert.True(Math.Abs(signal[start + (int)(0.4 * Rate) - 1]) < 0.05);
    }

    [Fact]
    public void NoiseBursts_RepeatForASeed_AndDifferWithAnother()
    {
        var a = DrySignals.NoiseBursts(Rate, 1, seed: 3);
        var b = DrySignals.NoiseBursts(Rate, 1, seed: 3);
        var c = DrySignals.NoiseBursts(Rate, 1, seed: 4);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(4000, 1)]
    [InlineData(22050, 0)]
    [InlineData(22050, 500)]
    public void UnusableLengthsOrRates_AreRefused(int rate, double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DrySignals.ClickTrain(rate, seconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => DrySignals.NoiseBursts(rate, seconds));
    }
}

public class RoomAuralisationTests
{
    private const int Rate = 22050;

    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    // A dead room, so that the direct sound dominates and the ear nearer the source is clearly the louder.
    private static Room3D DeadRoom() => Room3D.Shoebox(6, 4, 3, Finish(0.8));

    private static RayTraceOptions Quick() => new(RayCount: 20_000, ImageSourceOrder: 1);

    private static double Energy(double[] x) => x.Sum(v => v * v);

    [Fact]
    public void ASourceOnTheListenersLeft_IsLouderInTheLeftEar_AndTheOtherWayRound_WhenTheListenerTurns()
    {
        // Each rendering's ear levels vary by a dB or so with the noise the response is built from (see Auralisation),
        // so the level difference is judged over several seeds. Half a metre from the left ear the geometry gives about 3 dB.
        var room = DeadRoom();
        var source = ExtendedSource.Point(new Vec3(3.5, 2, 1.2));
        var head = new Vec3(4, 2, 1.2);
        var dry = DrySignals.ClickTrain(Rate, 1.0);

        double Mean(double facing) => Enumerable.Range(1, 5).Average(seed =>
            RoomAuralisation.Render(room, source, head, facing, dry, Rate, Quick() with { Seed = seed }).InterauralLevelDifferenceDb);

        var facingNorth = Mean(90);
        var facingSouth = Mean(270);

        Assert.True(facingNorth > 1, $"facing north {facingNorth:0.0} dB");
        Assert.True(facingSouth < -1, $"facing south {facingSouth:0.0} dB");
    }

    [Fact]
    public void TheEars_AreOnEitherSideOfTheHead_AtTheDefaultSpacing()
    {
        var result = RoomAuralisation.Render(DeadRoom(), ExtendedSource.Point(new Vec3(1, 1, 1.2)), new Vec3(4, 2, 1.2), 90,
            DrySignals.ClickTrain(Rate, 0.5), Rate, Quick());

        // Facing +y, the right-hand side is +x.
        Assert.Equal(3.915, result.LeftEar.X, 6);
        Assert.Equal(4.085, result.RightEar.X, 6);
        Assert.Equal(2, result.LeftEar.Y, 9);
    }

    [Fact]
    public void TheRendering_HasTheRequestedPeak_BothChannelsTheSameLength_AndALongerTailThanTheDrySound()
    {
        var dry = DrySignals.ClickTrain(Rate, 1.0);

        var result = RoomAuralisation.Render(DeadRoom(), ExtendedSource.Point(new Vec3(1, 1, 1.2)), new Vec3(4, 2, 1.2), 0,
            dry, Rate, Quick(), peak: 0.3);

        Assert.Equal(result.Left.Length, result.Right.Length);
        Assert.True(result.Left.Length > dry.Length);
        Assert.Equal(0.3, Math.Max(result.Left.Max(Math.Abs), result.Right.Max(Math.Abs)), 9);
        Assert.Equal(Rate, result.SampleRateHz);
        Assert.Equal(result.Left.Length / (double)Rate, result.DurationS, 9);
    }

    [Fact]
    public void ARendering_IsRepeatable()
    {
        var dry = DrySignals.ClickTrain(Rate, 0.6);
        var source = ExtendedSource.Point(new Vec3(1, 1, 1.2));

        var a = RoomAuralisation.Render(DeadRoom(), source, new Vec3(4, 2, 1.2), 0, dry, Rate, Quick());
        var b = RoomAuralisation.Render(DeadRoom(), source, new Vec3(4, 2, 1.2), 0, dry, Rate, Quick());

        Assert.Equal(a.Left, b.Left);
        Assert.Equal(a.Right, b.Right);
    }

    [Fact]
    public void TheEars_AreNotGivenTheSameNoise()
    {
        // On the axis between the ears the two responses have the same envelope, but the diffuse part is not a copy.
        var result = RoomAuralisation.Render(DeadRoom(), ExtendedSource.Point(new Vec3(1, 2, 1.2)), new Vec3(4, 2, 1.2), 0,
            DrySignals.ClickTrain(Rate, 0.6), Rate, Quick());

        Assert.NotEqual(result.Left, result.Right);
    }

    [Fact]
    public void ALineSource_CanBeRendered()
    {
        var source = ExtendedSource.Line(new Vec3(1, 1, 1.2), new Vec3(1, 3, 1.2), 4);

        var result = RoomAuralisation.Render(DeadRoom(), source, new Vec3(4, 2, 1.2), 180, DrySignals.ClickTrain(Rate, 0.6), Rate, Quick());

        Assert.All(result.Left, x => Assert.True(double.IsFinite(x)));
        Assert.True(Energy(result.Left) > 0 && Energy(result.Right) > 0);
    }

    [Fact]
    public void AListenerWithAnEarOutsideTheRoom_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => RoomAuralisation.Render(DeadRoom(), ExtendedSource.Point(new Vec3(1, 1, 1.2)),
            new Vec3(5.95, 2, 1.2), 90, DrySignals.ClickTrain(Rate, 0.5), Rate, Quick()));

        Assert.Contains("ears", ex.Message);
    }

    [Fact]
    public void UnusableSignalsOrRates_AreRefused()
    {
        var room = DeadRoom();
        var source = ExtendedSource.Point(new Vec3(1, 1, 1.2));
        var head = new Vec3(4, 2, 1.2);

        Assert.Throws<ArgumentException>(() => RoomAuralisation.Render(room, source, head, 0, Array.Empty<double>(), Rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomAuralisation.Render(room, source, head, 0, new double[100], 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomAuralisation.Render(room, source, head, 0, new double[100], Rate, peak: 0));
        Assert.Throws<ArgumentException>(() => RoomAuralisation.Render(room, source, head, 0, new double[8000 * 121], 8000));
    }
}
