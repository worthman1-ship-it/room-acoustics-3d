using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class EchoCriterionTests
{
    private const double Dt = 0.001;

    /// <summary>A direct sound at bin 10, then a diffuse tail that falls 60 dB in <paramref name="t60"/> seconds.</summary>
    private static double[] DirectAndTail(double t60 = 1.0, int bins = 1500)
    {
        var h = new double[bins];
        h[10] = 1;
        for (var i = 20; i < bins; i++) h[i] = 0.05 * Math.Pow(10, -6.0 * (i - 20) * Dt / t60);
        return h;
    }

    /// <summary>Adds an echo 5 ms long, five times the energy of the direct sound, <paramref name="delayMs"/> after it.</summary>
    private static void AddEcho(double[] h, int delayMs)
    {
        for (var i = 0; i < 5; i++) h[10 + delayMs + i] += 1.0;
    }

    [Fact]
    public void ASmoothDecay_StaysBelowTheLimits()
    {
        var speech = EchoCriterion.Evaluate(DirectAndTail(), Dt, EchoKind.Speech);
        var music = EchoCriterion.Evaluate(DirectAndTail(), Dt, EchoKind.Music);

        Assert.NotNull(speech);
        Assert.NotNull(music);
        Assert.True(speech!.Value < speech.Limit10Percent, $"speech {speech.Value}");
        Assert.True(music!.Value < music.Limit10Percent, $"music {music.Value}");
        Assert.False(speech.IsEcho);
        Assert.False(music.IsEcho);
    }

    [Fact]
    public void AStrongLateReflection_IsAnEcho_AndIsFoundAtItsDelay()
    {
        var h = DirectAndTail();
        AddEcho(h, 150);

        var speech = EchoCriterion.Evaluate(h, Dt, EchoKind.Speech)!;

        Assert.True(speech.IsEcho, $"EK {speech.Value}");
        Assert.InRange(speech.DelayS, 0.150 - 0.009 - 0.002, 0.150 + 0.002); // the window that holds the echo
    }

    [Fact]
    public void AnEchoMustBeAfter50ms_ToCountForSpeech_AndAfter80ms_ForMusic()
    {
        var early = DirectAndTail();
        AddEcho(early, 30);
        var between = DirectAndTail();
        AddEcho(between, 65);

        Assert.True(EchoCriterion.Evaluate(early, Dt, EchoKind.Speech)!.DelayS >= 0.05 - 1e-9);
        Assert.True(EchoCriterion.Evaluate(between, Dt, EchoKind.Music)!.DelayS >= 0.08 - 1e-9);
    }

    [Fact]
    public void TheCriterion_DoesNotDependOnLevelOrOnTheDelayOfTheDirectSound()
    {
        var h = DirectAndTail();
        AddEcho(h, 150);
        var louder = h.Select(e => e * 37).ToArray();
        var later = new double[h.Length + 40];
        Array.Copy(h, 0, later, 40, h.Length);

        var reference = EchoCriterion.Evaluate(h, Dt, EchoKind.Speech)!;

        Assert.Equal(reference.Value, EchoCriterion.Evaluate(louder, Dt, EchoKind.Speech)!.Value, 9);
        var shifted = EchoCriterion.Evaluate(later, Dt, EchoKind.Speech)!;
        Assert.Equal(reference.Value, shifted.Value, 9);
        Assert.Equal(reference.DelayS, shifted.DelayS, 9);
    }

    [Fact]
    public void TheLimits_AreThePublishedOnes()
    {
        Assert.Equal(1.0, EchoCriterion.Limit(EchoKind.Speech));
        Assert.Equal(0.9, EchoCriterion.Limit10Percent(EchoKind.Speech));
        Assert.Equal(1.8, EchoCriterion.Limit(EchoKind.Music));
        Assert.Equal(1.5, EchoCriterion.Limit10Percent(EchoKind.Music));
        Assert.Equal(0.05, EchoCriterion.FirstDelayS(EchoKind.Speech));
        Assert.Equal(0.08, EchoCriterion.FirstDelayS(EchoKind.Music));
    }

    [Fact]
    public void Curve_IsUndefinedAtTheStart_AndWhereTheWindowRunsOffTheEnd()
    {
        var curve = EchoCriterion.Curve(DirectAndTail(bins: 300), Dt, EchoKind.Speech);

        Assert.True(double.IsNaN(curve[0]));
        Assert.True(double.IsNaN(curve[^1]));
        Assert.False(double.IsNaN(curve[100]));
        Assert.Equal(300 - 10, curve.Length); // from the first arrival
    }

    [Fact]
    public void NothingToMeasure_GivesNull()
    {
        Assert.Null(EchoCriterion.Evaluate(new double[500], Dt, EchoKind.Speech));
        Assert.Null(EchoCriterion.Evaluate(Array.Empty<double>(), Dt, EchoKind.Speech));
        Assert.Null(EchoCriterion.Evaluate(DirectAndTail(bins: 40), Dt, EchoKind.Speech)); // ends before 50 ms
    }

    [Fact]
    public void ABadBinWidth_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EchoCriterion.Curve(new double[10], 0, EchoKind.Speech));
        Assert.Throws<ArgumentNullException>(() => EchoCriterion.Curve(null!, Dt, EchoKind.Speech));
    }

    [Fact]
    public void TheCalculator_ReportsTheSameValuesInEveryBand()
    {
        var h = DirectAndTail();
        AddEcho(h, 150);
        var ir = new ImpulseResponse(Dt, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));

        var metrics = RoomAcousticsCalculator.Calculate(ir);

        Assert.Equal(EchoCriterion.Evaluate(h, Dt, EchoKind.Speech)!.Value, metrics.EchoCriterionSpeech.AtFrequency(1000), 12);
        Assert.Equal(EchoCriterion.Evaluate(h, Dt, EchoKind.Music)!.Value, metrics.EchoCriterionMusic.AtFrequency(250), 12);
    }

    private static SurfaceFinish Finish(double alpha, double scattering = 0) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scattering));

    [Fact]
    public void ALongRoomWithAHardRearWall_ShowsAnEcho_ThatAnEvenlyAbsorbingRoomDoesNot()
    {
        var absorbing = Finish(0.9);
        var hardRear = Room3D.Extrude(new[] { (0.0, 0.0), (60.0, 0.0), (60.0, 10.0), (0.0, 10.0) }, 4, absorbing, absorbing, absorbing);
        // Wall 2 of the extrusion is the far end (x = 60).
        var far = hardRear.Faces.Select((f, i) => (f, i)).Single(t => t.f.Vertices.All(v => Math.Abs(v.X - 60) < 1e-9)).i;
        var faces = hardRear.Faces.Select((f, i) => new PlanarFace(f.Name, f.Vertices, i == far ? Finish(0.05) : absorbing)).ToList();
        var echoRoom = new Room3D(faces);
        var plainRoom = Room3D.Shoebox(60, 10, 4, Finish(0.5));
        var source = new Vec3(3, 5, 1.5);
        var receiver = new Vec3(8, 5, 1.2);
        var options = new RayTraceOptions(RayCount: 2000, ImageSourceOrder: 1);

        var echo = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(echoRoom, source, receiver, options));
        var plain = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(plainRoom, source, receiver, options));

        Assert.True(echo.EchoCriterionSpeech.AtFrequency(1000) > 1, $"echo room EK {echo.EchoCriterionSpeech.AtFrequency(1000)}");
        Assert.True(plain.EchoCriterionSpeech.AtFrequency(1000) < 1, $"plain room EK {plain.EchoCriterionSpeech.AtFrequency(1000)}");
    }
}

public class ArrivalDirectionTests
{
    private static SurfaceFinish Finish() =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)0.2, 8).ToArray()), OctaveSpectrum.Flat(0));

}

public class BilliardRaysTests
{
    private static SurfaceFinish Finish(double alpha = 0.2, double scattering = 0) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scattering));

    private static Room3D Box(double alpha = 0.2, double scattering = 0) => Room3D.Shoebox(10, 7, 3, Finish(alpha, scattering));

    private static readonly Vec3 Source = new(2, 2, 1.5);

    [Fact]
    public void TheOverallResponse_LosesTheMeanOfTheAbsorptionOf500HzAnd1kHz_AtEachBounce()
    {
        // 500 Hz absorbs 0.1 and 1 kHz 0.5: what a bounce leaves is the mean of 0.9 and 0.5, which is 0.7
        var alphas = Enumerable.Repeat((double?)0.2, 8).ToArray();
        alphas[3] = 0.1; alphas[4] = 0.5;
        var room = Room3D.Shoebox(10, 7, 3, new SurfaceFinish(new AbsorptionMaterial("test", "User", alphas), OctaveSpectrum.Flat(0)));
        double Left(int band) => BilliardRays.Trace(room, Source, 1, 1, band)[0].LevelsDb![^1];

        var overall = Left(BilliardRays.Overall);
        var linear = (Math.Pow(10, Left(3) / 10) + Math.Pow(10, Left(4) / 10)) / 2;
        Assert.InRange(Math.Abs(overall - 10 * Math.Log10(linear)), 0, 0.1);
        Assert.InRange(overall, Left(4), Left(3));
    }

    [Fact]
    public void EachRay_StartsAtTheSource_AndBouncesOffTheWalls()
    {
        var rays = BilliardRays.Trace(Box(), Source, 50, 5, 3);

        Assert.Equal(50, rays.Count);
        foreach (var ray in rays)
        {
            Assert.Equal(Source, ray.Points[0]);
            Assert.Equal(ray.Reflections + 2, ray.Points.Count); // the source, then each wall it hit, the last one included
            Assert.InRange(ray.Reflections, 0, 5);
        }
        Assert.Contains(rays, r => r.Reflections == 5);
    }

    [Fact]
    public void EveryBouncePoint_IsOnASurface_AndObeysTheLawOfReflection()
    {
        var room = Box();
        var rays = BilliardRays.Trace(room, Source, 40, 6, 3);

        foreach (var ray in rays)
            for (var i = 1; i + 1 < ray.Points.Count; i++)
            {
                var point = ray.Points[i];
                var incoming = (point - ray.Points[i - 1]).Normalized();
                var outgoing = (ray.Points[i + 1] - point).Normalized();
                var ok = room.Faces.Any(f =>
                    Math.Abs(f.InwardNormal.Dot(point - f.Vertices[0])) < 1e-6 &&
                    (outgoing - (incoming - f.InwardNormal * (2 * incoming.Dot(f.InwardNormal)))).Length < 1e-6);
                Assert.True(ok, $"bounce {i} is not a specular reflection off a surface");
            }
    }

    [Fact]
    public void TheSameSeedGivesTheSameRays_AndAnotherSeedGivesOthers()
    {
        var a = BilliardRays.Trace(Box(), Source, 20, 4, 3, new RayTraceOptions(Seed: 7));
        var b = BilliardRays.Trace(Box(), Source, 20, 4, 3, new RayTraceOptions(Seed: 7));
        var c = BilliardRays.Trace(Box(), Source, 20, 4, 3, new RayTraceOptions(Seed: 8));

        Assert.Equal(a.Select(r => r.Points[1]), b.Select(r => r.Points[1]));
        Assert.NotEqual(a.Select(r => r.Points[1]), c.Select(r => r.Points[1]));
    }

    [Fact]
    public void ScatteringSendsSomeRaysOffDiffusely_AndNoneWithout()
    {
        Assert.DoesNotContain(BilliardRays.Trace(Box(0.2, 0), Source, 100, 6, 3), r => r.Scattered);
        var rough = BilliardRays.Trace(Box(0.2, 0.6), Source, 100, 6, 3);
        Assert.Contains(rough, r => r.Scattered);
        Assert.Contains(rough, r => !r.Scattered);
    }

    [Fact]
    public void ARayStopsWhenItsEnergyIsGone()
    {
        var rays = BilliardRays.Trace(Box(alpha: 0.9), Source, 30, 100, 3, new RayTraceOptions(CutoffDb: 30));

        // 0.1 per bounce: 30 dB is gone after three bounces.
        Assert.All(rays, r => Assert.True(r.Reflections <= 3));
    }

    [Fact]
    public void EachPointCarriesTheLevelTheRayLeavesItWith_FallingByTheAbsorptionAtEveryBounce()
    {
        // half the energy is absorbed at each bounce (-3.01 dB); air absorption at 500 Hz over a few metres is a few hundredths of a dB
        var rays = BilliardRays.Trace(Box(alpha: 0.5), Source, 40, 4, 3);

        foreach (var ray in rays)
        {
            Assert.NotNull(ray.LevelsDb);
            Assert.Equal(ray.Points.Count, ray.LevelsDb!.Count);
            Assert.Equal(0, ray.LevelsDb[0], 9); // an omnidirectional source
            for (var i = 1; i < ray.LevelsDb.Count - 1; i++) // each bounce but the last point, where the ray ended and carries its arrival level
                Assert.InRange(ray.LevelsDb[i] - ray.LevelsDb[i - 1], -3.01 - 0.3, -3.01);
        }
        Assert.Contains(rays, r => r.Reflections == 4);
    }

    [Fact]
    public void WithAReflectionLimitOfZero_EachRayIsTheStraightLegToTheFirstWall()
    {
        var rays = BilliardRays.Trace(Box(), Source, 10, 0, 3);

        Assert.All(rays, r => Assert.Equal(2, r.Points.Count));
        Assert.All(rays, r => Assert.Equal(0, r.Reflections));
    }

    [Fact]
    public void TheNumberOfRaysThroughAReceiver_IsTheShareItsSolidAngleTakes()
    {
        var receiver = new Vec3(5, 2, 1.5); // 3 m from the source
        var rays = BilliardRays.Trace(Room3D.Shoebox(10, 7, 3, Finish(1.0)), Source, 6000, 0, 3);

        var hits = rays.Count(r => BilliardRays.ReachesReceiver(r, receiver, 0.5));

        var expected = 6000 * (1 - Math.Sqrt(1 - Math.Pow(0.5 / 3, 2))) / 2;
        Assert.InRange(hits, expected - 5 * Math.Sqrt(expected), expected + 5 * Math.Sqrt(expected));
    }

    [Fact]
    public void ARayThatMissesTheReceiver_DoesNotReachIt()
    {
        var ray = new BilliardRay(new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) }, 0, false);

        Assert.True(BilliardRays.ReachesReceiver(ray, new Vec3(5, 0.4, 0), 0.5));
        Assert.False(BilliardRays.ReachesReceiver(ray, new Vec3(5, 0.6, 0), 0.5));
        Assert.False(BilliardRays.ReachesReceiver(ray, new Vec3(12, 0, 0), 0.5)); // beyond the end
    }

    [Fact]
    public void BadArguments_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BilliardRays.Trace(Box(), Source, 0, 3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BilliardRays.Trace(Box(), Source, BilliardRays.MaxRays + 1, 3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BilliardRays.Trace(Box(), Source, 5, -1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BilliardRays.Trace(Box(), Source, 5, 3, 8));
        Assert.Throws<ArgumentException>(() => BilliardRays.Trace(Box(), new Vec3(20, 2, 1), 5, 3, 3));
    }
}

public class ReflectorCoverageTests
{
    private static SurfaceFinish Finish(double alpha = 0.2, double scattering = 0) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scattering));

    private static Room3D Box(double alpha = 0.2) => Room3D.Shoebox(10, 7, 3, Finish(alpha));

    private static readonly Vec3 Source = new(2, 2, 1.5);
    private static readonly Vec3[] Receivers = { new(6, 4, 1.2), new(8, 5, 1.2), new(4, 1, 1.2) };

    private static int Face(Room3D room, string name) => room.Faces.Select((f, i) => (f, i)).Single(t => t.f.Name == name).i;

}
