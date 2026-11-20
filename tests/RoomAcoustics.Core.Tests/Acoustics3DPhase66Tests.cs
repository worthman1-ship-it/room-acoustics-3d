using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class ThirdOctaveSimulationTests
{
    private static readonly Vec3 Source = new(1.5, 1.2, 1.5);
    private static readonly Vec3 Receiver = new(4.2, 2.6, 1.2);

    private static Room3D Box(double?[] alpha) =>
        Room3D.Shoebox(6, 4, 3, new SurfaceFinish(new AbsorptionMaterial("test", "User", alpha), OctaveSpectrum.Flat(0.2)));

    private static RayTraceOptions Options() => new(RayCount: 40_000, ImageSourceOrder: 2, MaxTimeS: 2.0);

    [Fact]
    public void Interpolate_IsTheOctaveValueAtItsCentre_LinearInLogFrequencyBetween_AndHeldAtTheEnds()
    {
        var v = new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8 };
        for (var i = 0; i < 8; i++) Assert.Equal(v[i], ThirdOctaveSimulation.Interpolate(v, OctaveSpectrum.Frequencies[i]), 9);
        Assert.Equal(0.15, ThirdOctaveSimulation.Interpolate(v, Math.Sqrt(63.0 * 125)), 9);   // the geometric middle of two bands
        Assert.Equal(0.1, ThirdOctaveSimulation.Interpolate(v, 50), 9);
        Assert.Equal(0.8, ThirdOctaveSimulation.Interpolate(v, 10_000), 9);
        Assert.Throws<ArgumentException>(() => ThirdOctaveSimulation.Interpolate(new[] { 1.0, 2 }, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThirdOctaveSimulation.Interpolate(v, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThirdOctaveSimulation.Interpolate(v, double.NaN));
    }

    [Theory]
    [InlineData(50, 0)]
    [InlineData(80, 0)]
    [InlineData(100, 1)]
    [InlineData(1000, 4)]
    [InlineData(1250, 4)]
    [InlineData(1600, 5)]
    [InlineData(8000, 7)]
    [InlineData(10000, 7)]
    public void OctaveBandOf_IsTheNearestCentreOnALogScale(double hz, int band) =>
        Assert.Equal(band, ThirdOctaveSimulation.OctaveBandOf(hz));

    [Fact]
    public void ItGivesTheTwentyThreeBands_WithTheirNominalAndExactCentres()
    {
        var bands = ThirdOctaveSimulation.Calculate(Box(Enumerable.Repeat((double?)0.2, 8).ToArray()), ExtendedSource.Point(Source), Receiver, Options());

        Assert.Equal(23, bands.Count);
        Assert.Equal(MeasuredThirdOctaveResponse.NominalCentresHz, bands.Select(b => b.NominalHz).ToArray());
        Assert.Equal(1000, bands[13].ExactHz, 9);
        Assert.Equal(50.119, bands[0].ExactHz, 2);
    }

    [Fact]
    public void ARoomWithTheSameAbsorptionAtEveryFrequency_HasTheSameDecayInEveryBand_NearEyring()
    {
        var room = Box(Enumerable.Repeat((double?)0.2, 8).ToArray());
        var bands = ThirdOctaveSimulation.Calculate(room, ExtendedSource.Point(Source), Receiver, Options());
        var eyring = 0.161 * 72 / (-108 * Math.Log(1 - 0.2));   // 6 x 4 x 3 m

        // up to 2.5 kHz air absorption is small; the narrow low bands scatter more, so the limits are generous
        foreach (var b in bands.Where(b => b.NominalHz is >= 100 and <= 2500))
            Assert.InRange(b.T30S, eyring * 0.7, eyring * 1.3);
    }

    [Fact]
    public void WhereAbsorptionRisesWithFrequency_TheDecayFalls_SmoothlyThroughTheThirdOctaves()
    {
        var room = Box(new double?[] { 0.05, 0.08, 0.15, 0.25, 0.4, 0.55, 0.65, 0.7 });
        var bands = ThirdOctaveSimulation.Calculate(room, ExtendedSource.Point(Source), Receiver, Options());
        double T(int hz) => bands.Single(b => b.NominalHz == hz).T30S;

        Assert.True(T(125) > T(500) * 1.4);
        Assert.True(T(500) > T(2000) * 1.3);
        // a band between two octave centres lies between their decay times (a margin for the scatter of a tracing)
        Assert.InRange(T(160), T(250) * 0.9, T(125) * 1.1);
        Assert.InRange(T(1250), T(2000) * 0.85, T(1000) * 1.15);
    }

    [Fact]
    public void TheOneKilohertzThirdOctave_AgreesWithTheOctaveBandOfTheSameRoom()
    {
        var room = Box(new double?[] { 0.05, 0.08, 0.15, 0.25, 0.4, 0.55, 0.65, 0.7 });
        var options = Options();
        var third = ThirdOctaveSimulation.Calculate(room, ExtendedSource.Point(Source), Receiver, options).Single(b => b.NominalHz == 1000);
        var octave = AcousticMap.Calculate(room, Source, new[] { Receiver }, options).Metrics[0];

        Assert.InRange(third.T30S, octave.T30S.AtFrequency(1000) * 0.85, octave.T30S.AtFrequency(1000) * 1.15);
        Assert.InRange(third.C80Db, octave.C80Db.AtFrequency(1000) - 2.5, octave.C80Db.AtFrequency(1000) + 2.5);
        Assert.InRange(third.StrengthGDb, octave.StrengthGDb.AtFrequency(1000) - 1.5, octave.StrengthGDb.AtFrequency(1000) + 1.5);
    }

    [Fact]
    public void ADirectionalSource_RunsInThirdOctaves_AndGivesFiniteResults()
    {
        var room = Box(Enumerable.Repeat((double?)0.2, 8).ToArray());
        var source = ExtendedSource.Point(Source, SourceDirectivity.Cardioid(new Vec3(1, 0, 0)));
        var bands = ThirdOctaveSimulation.Calculate(room, source, Receiver, Options());

        Assert.All(bands.Where(b => b.NominalHz >= 100), b => Assert.True(double.IsFinite(b.T30S) && double.IsFinite(b.C80Db) && double.IsFinite(b.StrengthGDb)));
    }

    [Fact]
    public void BandCentres_MustBeEightPositiveFrequencies()
    {
        var room = Box(Enumerable.Repeat((double?)0.2, 8).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnergyRayTracer.TraceReceivers(room, Source, new[] { Receiver }, new RayTraceOptions(RayCount: 1000, BandCentresHz: new double[] { 100, 200 })));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnergyRayTracer.TraceReceivers(room, Source, new[] { Receiver }, new RayTraceOptions(RayCount: 1000, BandCentresHz: new double[] { 1, 2, 3, 4, 5, 6, 7, 0 })));
    }

    [Fact]
    public void AirAbsorption_FollowsTheBandCentres_NotTheOctaveSlot()
    {
        var room = Box(Enumerable.Repeat((double?)0.05, 8).ToArray());   // a live room, so air absorption matters
        var options = new RayTraceOptions(RayCount: 30_000, MaxTimeS: 4.0, ImageSourceOrder: 0);
        // every slot at 20 kHz (strong air absorption) against every slot at 100 Hz (almost none)
        var high = EnergyRayTracer.TraceReceivers(room, Source, new[] { Receiver }, options with { BandCentresHz = Enumerable.Repeat(20000.0, 8).ToArray() })[0];
        var low = EnergyRayTracer.TraceReceivers(room, Source, new[] { Receiver }, options with { BandCentresHz = Enumerable.Repeat(100.0, 8).ToArray() })[0];

        Assert.True(high.Energy[0].Sum() < low.Energy[0].Sum() * 0.8);
    }
}

public class ExampleDxfTests
{
    private static string ExamplePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples", "LectureTheatre.dxf");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("examples/LectureTheatre.dxf was not found above the test folder.");
    }

    [Fact]
    public void TheExampleLectureTheatre_ImportsAsAClosedRoom_WithOneSurfaceGroupPerLayer()
    {
        var result = RoomAcoustics.Core.CadMeshReader.ReadFile(ExamplePath());

        Assert.Empty(result.Skipped);
        Assert.Equal(new[] { "BACK_WALL", "CEILING", "FRONT_WALL", "SEATING", "SIDE_WALLS", "STAGE", "STAGE_FRONT" }, result.Mesh.Groups.OrderBy(g => g).ToArray());
        var room = result.Mesh.ToRoom(_ => new SurfaceFinish(new AbsorptionMaterial("m", "User", Enumerable.Repeat((double?)0.2, 8).ToArray()), OctaveSpectrum.Flat(0.1)));
        Assert.Equal(910.8, room.VolumeM3, 1);
    }

    [Fact]
    public void TheExampleLectureTheatre_CanBeSimulated_FromTheStageToTheSeating()
    {
        var room = RoomAcoustics.Core.CadMeshReader.ReadFile(ExamplePath()).Mesh
            .ToRoom(_ => new SurfaceFinish(new AbsorptionMaterial("m", "User", Enumerable.Repeat((double?)0.2, 8).ToArray()), OctaveSpectrum.Flat(0.1)));
        var map = AcousticMap.Calculate(room, new Vec3(2, 6, 2.0), new[] { new Vec3(10, 6, 2.6) }, new RayTraceOptions(RayCount: 20_000, ImageSourceOrder: 2));

        Assert.True(double.IsFinite(map.Metrics[0].T30S.AtFrequency(1000)));
        Assert.InRange(map.Metrics[0].T30S.AtFrequency(1000), 0.5, 3.0);
    }
}
