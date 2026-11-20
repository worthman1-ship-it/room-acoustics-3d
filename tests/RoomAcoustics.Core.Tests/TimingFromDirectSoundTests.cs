using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>ISO 3382-1 times its parameters from the arrival of the direct sound, so a receiver further from the source (the same response, later) gives the same values.</summary>
public class TimingFromDirectSoundTests
{
    private const double Dt = 0.001;

    /// <summary>A direct sound at <paramref name="delayS"/>, then an exponential decay of reverberation time 1.5 s, in every band.</summary>
    private static RoomAcousticMetrics Response(double delayS)
    {
        var h = new double[3000];
        var start = (int)Math.Round(delayS / Dt);
        h[start] = 1;
        for (var i = 1; start + i < h.Length; i++) h[start + i] = 0.05 * Math.Exp(-13.8155 * i * Dt / 1.5);
        return RoomAcousticsCalculator.Calculate(new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1)));
    }

    [Fact]
    public void TheEarlyWindowsAndTheDecay_RunFromTheDirectSound_NotFromTheEmission()
    {
        var near = Response(0.0);
        var far = Response(0.018); // 6 m further: 18 ms later

        Assert.Equal(near.C50Db[3], far.C50Db[3], 9);
        Assert.Equal(near.C80Db[3], far.C80Db[3], 9);
        Assert.Equal(near.D50[3], far.D50[3], 9);
        Assert.Equal(near.CentreTimeS[3], far.CentreTimeS[3], 9);
        Assert.Equal(near.EarlyDecayTimeS[3], far.EarlyDecayTimeS[3], 9);
        Assert.Equal(near.T20S[3], far.T20S[3], 9);
        Assert.Equal(near.EarlyStrengthGDb[3], far.EarlyStrengthGDb[3], 9);
    }
}
