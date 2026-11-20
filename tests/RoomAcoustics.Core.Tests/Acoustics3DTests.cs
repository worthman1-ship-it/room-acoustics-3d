using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class Acoustics3DGeometryTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()));

    [Fact]
    public void Shoebox_VolumeAndAreaAreExact()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.1));

        Assert.Equal(210, room.VolumeM3, 9);
        Assert.Equal(242, room.TotalAreaM2, 9); // 2 * (70 + 30 + 21)
        Assert.Equal(6, room.Faces.Count);
    }

    [Fact]
    public void Shoebox_InwardNormalsPointIntoTheRoom()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.1));

        Assert.Equal(new Vec3(0, 0, 1), room.Faces.Single(f => f.Name == "Floor").InwardNormal);
        Assert.Equal(new Vec3(0, 0, -1), room.Faces.Single(f => f.Name == "Ceiling").InwardNormal);
        Assert.True(room.Contains(new Vec3(5, 3.5, 1.5)));
        Assert.False(room.Contains(new Vec3(5, 3.5, 3.5)));
    }

    [Fact]
    public void Constructor_RejectsAnOpenRoom()
    {
        var box = Room3D.Shoebox(10, 7, 3, Finish(0.1)).Faces;
        var open = new List<PlanarFace>();
        foreach (var face in box.Where(f => f.Name != "Ceiling"))
            open.Add(new PlanarFace(face.Name, face.Vertices, face.Finish));

        Assert.Throws<ArgumentException>(() => new Room3D(open));
    }

    [Fact]
    public void Extrude_AcceptsANonConvexFootprint_WithTheRightVolumeAndArea()
    {
        var finish = Finish(0.1);
        var lShape = new[] { (0.0, 0.0), (4.0, 0.0), (4.0, 1.0), (1.0, 1.0), (1.0, 4.0), (0.0, 4.0) };

        var room = Room3D.Extrude(lShape, 3, finish, finish, finish);

        Assert.False(room.IsConvex);
        Assert.Equal(7 * 3, room.VolumeM3, 9);                 // L footprint area 4 + 3 = 7 m2
        Assert.Equal(2 * 7 + 3 * (4 + 1 + 3 + 3 + 1 + 4), room.TotalAreaM2, 9);
    }

    [Fact]
    public void Extrude_MatchesShoeboxForARectangularFootprint()
    {
        var finish = Finish(0.1);
        var footprint = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 7.0), (0.0, 7.0) };

        var extruded = Room3D.Extrude(footprint, 3, finish, finish, finish);

        Assert.Equal(Room3D.Shoebox(10, 7, 3, finish).VolumeM3, extruded.VolumeM3, 9);
    }
}

public class EnergyRayTracerTests
{
    private const int Band500 = 3;
    private const int Band1000 = 4;
    private const int Band8000 = 7;

    private static readonly Vec3 Source = new(2, 2, 1.5);
    private static readonly Vec3 Receiver = new(7, 5, 1.2);

    private static SurfaceFinish Finish(double alpha, double? scatter = null) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()),
            scatter is { } s ? OctaveSpectrum.Flat(s) : null);

    /// <summary>Surfaces for the existing reverberation calculator, so the reference is computed by the app's own formulas.</summary>
    private static IReadOnlyList<Surface> SurfacesOf(Room3D room) =>
        room.Faces.Select(f => new Surface(f.Name, f.AreaM2, f.Finish.Material)).ToList();

    private static double Eyring(Room3D room, int band, bool withAir = false) =>
        ReverberationCalculator.Calculate(room.VolumeM3, SurfacesOf(room),
            new ReverberationOptions(ReverberationMethod.EyringNorris, IncludeAirAbsorption: withAir)).T60[band];

    private static double Sabine(Room3D room, int band) =>
        ReverberationCalculator.CalculateT60(room.VolumeM3, SurfacesOf(room))[band];

    private static RoomAcousticMetrics Measure(Room3D room, RayTraceOptions? options = null) =>
        RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(room, Source, Receiver, options));

    [Fact]
    public void DecayTime_WithDiffuseReflection_AgreesWithEyringNorris()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, scatter: 1));

        var t30 = Measure(room).T30S[Band500];

        AssertBetweenEyringAndSabine(t30, room, Band500);
    }

    [Fact]
    public void DecayTime_AtHighAbsorption_FollowsEyringNotSabine()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.3, scatter: 1));

        var t30 = Measure(room).T30S[Band1000];

        // With α = 0.3 Eyring and Sabine differ by about 19%, so the ray result must be clearly on the Eyring side.
        AssertBetweenEyringAndSabine(t30, room, Band1000);
        Assert.True(Math.Abs(t30 - Eyring(room, Band1000)) < Math.Abs(t30 - Sabine(room, Band1000)));
    }

    /// <summary>
    /// Measured behaviour: the diffuse-ray decay lies from Eyring towards Sabine, about a third of the way
    /// across (stable across seeds and ray counts). Eyring is the lower bound; the upper bound is the midpoint.
    /// </summary>
    private static void AssertBetweenEyringAndSabine(double t, Room3D room, int band)
    {
        var eyring = Eyring(room, band);
        var sabine = Sabine(room, band);
        Assert.InRange(t, 0.98 * eyring, eyring + 0.5 * (sabine - eyring));
    }

    [Fact]
    public void DecayTime_WithPureSpecularReflectionInARectangularRoom_IsSlowerThanEyring()
    {
        // Specular reflection keeps each direction component's magnitude fixed, so the rays never randomise and
        // the decay is a superposition of slower rates. This is the known limitation the scattering coefficient addresses.
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, scatter: 0));

        var specular = Measure(room).T30S[Band500];

        Assert.True(specular > 1.2 * Eyring(room, Band500),
            $"Specular T30 {specular:0.000} s should exceed Eyring {Eyring(room, Band500):0.000} s by more than 20% in a rectangular room.");
    }

    [Fact]
    public void DecayTime_WithAirAbsorption_MatchesEyringWithFourMV()
    {
        // At 8 kHz air absorption dominates the decay of a room this size; the reference includes it through 4mV.
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.1, scatter: 1));

        var t30 = Measure(room).T30S[Band8000];
        var withAir = Eyring(room, Band8000, withAir: true);
        var withoutAir = Eyring(room, Band8000);

        Assert.True(withAir < 0.9 * withoutAir, "Air absorption should shorten the 8 kHz decay noticeably in this room.");
        Assert.InRange(t30 / withAir, 0.95, 1.05);
    }

    [Fact]
    public void Trace_IsDeterministicForAGivenSeed()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, scatter: 0.5));
        var options = new RayTraceOptions(RayCount: 20_000, Seed: 42);

        var first = EnergyRayTracer.Trace(room, Source, Receiver, options);
        var second = EnergyRayTracer.Trace(room, Source, Receiver, options);

        for (var b = 0; b < 8; b++)
            Assert.Equal(first.Energy[b], second.Energy[b]);
    }

    [Fact]
    public void Trace_RejectsAReceiverTooCloseToAWall()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2));

        Assert.Throws<ArgumentException>(() =>
            EnergyRayTracer.Trace(room, Source, new Vec3(7, 5, 0.2), new RayTraceOptions(RayCount: 1000)));
    }

    [Fact]
    public void Room_RejectsAnAbsorptionCoefficientAboveOne()
    {
        Assert.Throws<ArgumentException>(() => Room3D.Shoebox(10, 7, 3, Finish(1.5)));
    }

    [Fact]
    public void Trace_ReferenceEnergyAt10m_IsTheFreeFieldValueWithAir()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2));

        var ir = EnergyRayTracer.Trace(room, Source, Receiver, new RayTraceOptions(RayCount: 1000));

        // The receiver sphere's solid angle over 4π at 10 m (r = 0.5 m), and air absorption over 10 m at 63 Hz.
        var air = AirAbsorption.AttenuationDbPerMetre(63, AirAbsorption.ReferencePressurePa, 20, 60);
        var expected = (1 - Math.Sqrt(1 - 0.25 / 100)) / 2 * Math.Pow(10, -air * 10 / 10);
        Assert.Equal(expected, ir.ReferenceEnergy10m[0], 12);
    }
}

public class RoomAcousticMetricsTests
{
    private const double BinWidth = 0.001;

    private static ImpulseResponse ExponentialDecay(double t60, double totalSeconds = 2.0)
    {
        var bins = (int)(totalSeconds / BinWidth);
        var k = 6 * Math.Log(10) / t60; // energy decays by 60 dB in t60 seconds
        var h = Enumerable.Range(0, bins).Select(i => Math.Exp(-k * (i + 0.5) * BinWidth) * BinWidth).ToArray();
        return new ImpulseResponse(BinWidth, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));
    }

    [Fact]
    public void ExponentialDecay_GivesTheExactReverberationTimesForEveryDefinition()
    {
        var metrics = RoomAcousticsCalculator.Calculate(ExponentialDecay(0.5));

        Assert.InRange(metrics.EarlyDecayTimeS[0], 0.4995, 0.5005);
        Assert.InRange(metrics.T20S[0], 0.4995, 0.5005);
        Assert.InRange(metrics.T30S[0], 0.4995, 0.5005);
    }

    [Fact]
    public void Clarity_AndDefinition_MatchHandComputedValues()
    {
        // One unit of energy at 10 ms (inside 50 and 80 ms) and one at 200 ms (outside both).
        var h = new double[2000];
        h[10] = 1;
        h[200] = 1;
        var ir = new ImpulseResponse(BinWidth, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(1));

        var metrics = RoomAcousticsCalculator.Calculate(ir);

        Assert.Equal(0, metrics.C50Db[0], 9);      // 10 log10(1/1)
        Assert.Equal(0, metrics.C80Db[0], 9);
        Assert.Equal(0.5, metrics.D50[0], 9);
        // the centre time, like the windows, runs from the first arrival (10 ms), as ISO 3382-1 times everything from the direct sound
        Assert.Equal(((10.5 + 200.5) / 2 - 10) * BinWidth, metrics.CentreTimeS[0], 9);
    }

    [Fact]
    public void StrengthG_IsZeroWhenTheReceivedEnergyEqualsTheTenMetreReference()
    {
        var h = new double[1000];
        h[5] = 3;
        var ir = new ImpulseResponse(BinWidth, Enumerable.Repeat(h, 8).ToArray(), OctaveSpectrum.Flat(3));

        var metrics = RoomAcousticsCalculator.Calculate(ir);

        Assert.Equal(0, metrics.StrengthGDb[0], 9);
    }
}
