using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class EarlyLateStrengthTests
{
    private const double Dt = 0.001;

    /// <summary>An energy response with the given (time s, energy) arrivals in every band, and a unit free-field reference.</summary>
    private static ImpulseResponse Arrivals(params (double TimeS, double Energy)[] arrivals)
    {
        var h = new double[2000];
        foreach (var (t, e) in arrivals) h[(int)Math.Round(t / Dt)] += e;
        return new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));
    }

    [Fact]
    public void EarlyAndLateStrength_SplitTheEnergyAt80Ms()
    {
        var m = RoomAcousticsCalculator.Calculate(Arrivals((0.010, 1.0), (0.100, 0.25)));

        Assert.Equal(0, m.EarlyStrengthGDb[4], 9);
        Assert.Equal(10 * Math.Log10(0.25), m.LateStrengthGDb[4], 9);
        Assert.Equal(10 * Math.Log10(1.25), m.StrengthGDb[4], 9);
    }

    [Fact]
    public void EarlyMinusLateStrength_IsC80()
    {
        var m = RoomAcousticsCalculator.Calculate(Arrivals((0.010, 1.0), (0.030, 0.5), (0.100, 0.25), (0.400, 0.1)));

        Assert.Equal(m.C80Db[3], m.EarlyStrengthGDb[3] - m.LateStrengthGDb[3], 9);
    }

    [Fact]
    public void EarlyAndLateStrength_AddAsEnergyToG()
    {
        var m = RoomAcousticsCalculator.Calculate(Arrivals((0.010, 1.0), (0.030, 0.5), (0.100, 0.25)));

        var sum = 10 * Math.Log10(Math.Pow(10, m.EarlyStrengthGDb[2] / 10) + Math.Pow(10, m.LateStrengthGDb[2] / 10));
        Assert.Equal(m.StrengthGDb[2], sum, 9);
    }

    [Fact]
    public void LateStrength_IsNaN_WhenNothingArrivesLate()
    {
        var m = RoomAcousticsCalculator.Calculate(Arrivals((0.010, 1.0)));

        Assert.True(double.IsNaN(m.LateStrengthGDb[4]));
    }
}

public class LateralMetricsTests
{
    private const double Dt = 0.001;

    private static ImpulseResponse WithLateral((double TimeS, double Energy, double Cos)[] arrivals, double reference = 1)
    {
        double[] Bins(Func<(double TimeS, double Energy, double Cos), double> weight)
        {
            var h = new double[2000];
            foreach (var a in arrivals) h[(int)Math.Round(a.TimeS / Dt)] += weight(a);
            return h;
        }

        var energy = Bins(a => a.Energy);
        var lateral = Bins(a => a.Energy * a.Cos * a.Cos);
        var amplitude = Bins(a => a.Energy * Math.Abs(a.Cos));
        return new ImpulseResponse(Dt, Enumerable.Repeat(energy, 8).ToArray(), OctaveSpectrum.Flat(reference),
            Enumerable.Repeat(lateral, 8).ToArray(), Enumerable.Repeat(amplitude, 8).ToArray());
    }

    [Fact]
    public void LF80_IsTheLateralEnergyFrom5To80Ms_OverAllEnergyTo80Ms()
    {
        // 2 ms: fully lateral but before 5 ms, so only in the denominator. 10 ms: fully lateral. 20 ms: straight ahead.
        var ir = WithLateral(new[] { (0.002, 1.0, 1.0), (0.010, 1.0, 1.0), (0.020, 2.0, 0.0) });

        var m = RoomAcousticsCalculator.Calculate(ir);

        Assert.Equal(1.0 / 4.0, m.LateralFraction80[3], 9);
    }

    [Fact]
    public void LFC80_WeightsByAbsoluteCosine_SoItExceedsLF80()
    {
        // the direct sound from straight ahead at 0 ms, then two reflections from either side
        var ir = WithLateral(new[] { (0.000, 2.0, 0.0), (0.010, 1.0, 0.5), (0.020, 1.0, -0.5) });

        var m = RoomAcousticsCalculator.Calculate(ir);

        Assert.Equal((0.25 + 0.25) / 4.0, m.LateralFraction80[4], 9);
        Assert.Equal((0.5 + 0.5) / 4.0, m.LateralFractionCoefficient80[4], 9);
    }

    [Fact]
    public void LJ_IsTheLateralEnergyAfter80Ms_InDbAgainstTheTenMetreReference()
    {
        var ir = WithLateral(new[] { (0.010, 1.0, 1.0), (0.100, 1.0, Math.Sqrt(0.5)), (0.300, 1.0, 0.0) }, reference: 2);

        var m = RoomAcousticsCalculator.Calculate(ir);

        Assert.Equal(10 * Math.Log10(0.5 / 2), m.LateLateralLevelLJDb[5], 9);
    }

    [Fact]
    public void LateralMetrics_AreNaN_WithoutArrivalDirections()
    {
        var h = new double[2000];
        h[10] = 1;
        var ir = new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));

        var m = RoomAcousticsCalculator.Calculate(ir);

        Assert.True(double.IsNaN(m.LateralFraction80[4]));
        Assert.True(double.IsNaN(m.LateralFractionCoefficient80[4]));
        Assert.True(double.IsNaN(m.LateLateralLevelLJDb[4]));
    }

    private static SurfaceFinish Walls(double alpha, double scatter) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scatter));

    [Fact]
    public void ImageArrival_FinalDirection_IsTheLastLegIntoTheReceiver()
    {
        var room = Room3D.Shoebox(10, 7, 3, Walls(0.2, 0));
        var source = new Vec3(2, 3.5, 1.5);
        var receiver = new Vec3(6, 3.5, 1.5);

        var arrivals = ImageSourceEnumerator.Enumerate(room, source, receiver, 1);

        // The wall at y = 0 reflects the sound to the receiver from the image at y = -3.5: heading (4, 7) in plan.
        var viaSideWall = arrivals.Single(a => a.Faces.Count == 1 && Math.Abs(a.FirstReflection.Y) < 1e-9);
        Assert.Equal(4 / Math.Sqrt(65), viaSideWall.FinalDirection.X, 9);
        Assert.Equal(7 / Math.Sqrt(65), viaSideWall.FinalDirection.Y, 9);
        Assert.Equal(0, viaSideWall.FinalDirection.Z, 9);
        Assert.All(arrivals, a => Assert.Equal(1, a.FinalDirection.Length, 9));
    }

    [Fact]
    public void TracedLateralEnergy_NeverExceedsEnergy_AndAbsoluteCosineNeverFallsBelowCosineSquared()
    {
        var room = Room3D.Shoebox(10, 7, 3, Walls(0.2, 0.5));
        var options = new RayTraceOptions(RayCount: 30_000, ImageSourceOrder: 2);

        var ir = EnergyRayTracer.Trace(room, new Vec3(2, 3.5, 1.5), new Vec3(6, 2.5, 1.2), options);

        for (var band = 0; band < 8; band++)
            for (var i = 0; i < ir.Energy[band].Length; i++)
            {
                Assert.True(ir.LateralEnergy![band][i] <= ir.Energy[band][i] * (1 + 1e-12));
                Assert.True(ir.LateralAmplitudeEnergy![band][i] >= ir.LateralEnergy[band][i] * (1 - 1e-12));
            }
    }

    [Fact]
    public void TheDirectSound_HasNoLateralEnergy_BecauseTheAxisIsAcrossTheLineToTheSource()
    {
        // Floor, ceiling and walls absorb everything, so the response is the direct sound alone.
        var room = Room3D.Shoebox(10, 7, 3, Walls(1.0, 0));
        var options = new RayTraceOptions(RayCount: 2000);

        var ir = EnergyRayTracer.Trace(room, new Vec3(2, 2, 1.5), new Vec3(7, 5, 1.5), options);

        var direct = ir.Energy[4].Sum();
        Assert.True(direct > 0);
        Assert.Equal(0, ir.LateralEnergy![4].Sum() / direct, 6);
    }

    [Fact]
    public void ADiffuseReverberantField_HasOneThirdOfItsLateEnergyAlongAnyAxis()
    {
        var room = Room3D.Shoebox(10, 7, 3, Walls(0.1, 0.5));
        var options = new RayTraceOptions(RayCount: 200_000, ImageSourceOrder: 2);

        var ir = EnergyRayTracer.Trace(room, new Vec3(2, 3.5, 1.5), new Vec3(6, 2.5, 1.2), options);

        double late = 0, lateral = 0;
        for (var i = 100; i < ir.Energy[4].Length; i++)
        {
            late += ir.Energy[4][i];
            lateral += ir.LateralEnergy![4][i];
        }
        Assert.InRange(lateral / late, 0.29, 0.37);
    }
}

public class LevelAndIntelligibilityTests
{
    private const double Dt = 0.001;

    private static ImpulseResponse Impulse(double energy)
    {
        var h = new double[4000];
        h[0] = energy;
        return new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));
    }

    /// <summary>Energy decaying at 60 dB in <paramref name="reverberationTimeS"/>: exp(-13.8 t / T).</summary>
    private static ImpulseResponse ExponentialDecay(double reverberationTimeS)
    {
        var h = new double[4000];
        for (var i = 0; i < h.Length; i++) h[i] = Math.Exp(-13.8155 * (i + 0.5) * Dt / reverberationTimeS);
        var total = h.Sum();
        for (var i = 0; i < h.Length; i++) h[i] /= total; // unit energy, so G = 0 dB against a unit reference
        return new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));
    }

    [Fact]
    public void SoundPressureLevel_IsPowerLevelPlusStrengthMinus31()
    {
        var metrics = RoomAcousticsCalculator.Calculate(Impulse(4)); // G = 10 lg 4 = 6.02 dB

        var lp = RoomLevels.SoundPressureLevels(metrics, OctaveSpectrum.Flat(90));

        Assert.Equal(90 + 10 * Math.Log10(4) - 31, lp[4], 9);
    }

    [Fact]
    public void AFreeFieldSourceAt10m_GivesLwMinus31()
    {
        // Strength 0 dB is, by definition, the free-field level at 10 m, which for Lw is Lw - 20 lg 10 - 11.
        var lp = RoomLevels.SoundPressureLevels(RoomAcousticsCalculator.Calculate(Impulse(1)), OctaveSpectrum.Flat(100));

        Assert.Equal(100 - 20 * Math.Log10(10) - 11, lp[2], 9);
    }

    [Fact]
    public void CWeighting_OfAFlatSpectrum_IsTheEnergySumOfTheBandCorrections()
    {
        var expected = 60 + 10 * Math.Log10(Math.Pow(10, -0.08) + Math.Pow(10, -0.02) + 3 + Math.Pow(10, -0.02) + Math.Pow(10, -0.08) + Math.Pow(10, -0.3));

        Assert.Equal(expected, AcousticMath.DbC(OctaveSpectrum.Flat(60)), 9);
        Assert.Equal(68.4967, AcousticMath.DbC(OctaveSpectrum.Flat(60)), 3);
    }

    [Fact]
    public void ModulationTransfer_OfASingleImpulse_IsOneAtEveryModulationFrequency()
    {
        var h = new double[4000];
        h[0] = 3;

        foreach (var fm in SpeechTransmissionIndex.Modulations)
            Assert.Equal(1, RoomLevels.ModulationTransfer(h, Dt, fm), 2);
    }

    [Theory]
    [InlineData(0.6, 1.25)]
    [InlineData(1.5, 4.0)]
    [InlineData(2.5, 12.5)]
    public void ModulationTransfer_OfAnExponentialDecay_MatchesTheClosedForm(double reverberationTimeS, double fm)
    {
        var ir = ExponentialDecay(reverberationTimeS);

        var measured = RoomLevels.ModulationTransfer(ir.Energy[4], Dt, fm);

        var expected = 1 / Math.Sqrt(1 + Math.Pow(2 * Math.PI * fm * reverberationTimeS / 13.8155, 2));
        Assert.Equal(expected, measured, 2);
    }

    [Fact]
    public void Sti_OfADrySignalWellAboveTheNoise_IsNearOne()
    {
        var sti = RoomLevels.SpeechTransmissionIndex(Impulse(1), OctaveSpectrum.Flat(96), OctaveSpectrum.Flat(30));

        Assert.InRange(sti, 0.95, 1.0);
    }

    [Fact]
    public void Sti_FallsAsTheRoomGetsMoreReverberant()
    {
        var speech = OctaveSpectrum.Flat(96);
        var noise = OctaveSpectrum.Flat(30);

        var dry = RoomLevels.SpeechTransmissionIndex(ExponentialDecay(0.4), speech, noise);
        var live = RoomLevels.SpeechTransmissionIndex(ExponentialDecay(1.5), speech, noise);
        var cathedral = RoomLevels.SpeechTransmissionIndex(ExponentialDecay(4.0), speech, noise);

        Assert.True(dry > live && live > cathedral, $"{dry} {live} {cathedral}");
        Assert.True(dry > 0.6);
        Assert.True(cathedral < 0.4);
    }

    [Fact]
    public void Sti_FromTheImpulseResponse_AgreesWithTheReverberationTimeMethodForADiffuseDecay()
    {
        // The same decay through both routes: the closed-form modulation transfer of the RT method, and the one measured
        // from the simulated response. The two should land within a few hundredths.
        var speech = OctaveSpectrum.Flat(96);
        var noise = OctaveSpectrum.Flat(30);
        var ir = ExponentialDecay(1.2);

        var fromResponse = RoomLevels.SpeechTransmissionIndex(ir, speech, noise);

        var levels = RoomLevels.SoundPressureLevels(RoomAcousticsCalculator.Calculate(ir), speech);
        var fromRt = SpeechTransmissionIndex.Calculate(levels, noise, OctaveSpectrum.Flat(1.2));
        Assert.Equal(fromRt, fromResponse, 1);
    }

    [Fact]
    public void ExistingReverberationTimeSti_IsUnchangedByTheRefactoring()
    {
        // The IEC predictor value the module has always returned for these inputs.
        var viaCalculate = SpeechTransmissionIndex.Calculate(
            SpeechTransmissionIndex.PredictorSpeech(60), NoiseCriterion.Curve(35), SpeechTransmissionIndex.PredictorReverberationTimes(1.0));

        Assert.Equal(SpeechTransmissionIndex.Predict(1.0, 35, 60), viaCalculate);
    }
}
