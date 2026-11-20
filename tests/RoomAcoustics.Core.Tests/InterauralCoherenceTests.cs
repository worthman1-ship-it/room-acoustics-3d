using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class InterauralCoherenceTests
{
    private static double[] From(params (double CosToLeft, double Energy)[] arrivals)
    {
        var cells = new double[InterauralCoherence.Cells];
        foreach (var (c, e) in arrivals) cells[InterauralCoherence.Cell(c)] += e;
        return cells;
    }

    /// <summary>Energy spread evenly over the sphere: as much from each step of the cosine to the ear axis.</summary>
    private static double[] Diffuse()
    {
        var cells = new double[InterauralCoherence.Cells];
        const int steps = 20000;
        for (var i = 0; i < steps; i++) cells[InterauralCoherence.Cell(-1 + 2 * (i + 0.5) / steps)] += 1;
        return cells;
    }

    [Theory]
    [InlineData(0)]      // straight ahead
    [InlineData(1)]      // straight from the left: the right ear hears it quieter and 0.66 ms later, but it is the same sound
    [InlineData(-0.5)]
    public void OneArrival_IsTheSameSoundAtBothEars_SoItsIaccIs1(double cosToLeft)
    {
        for (var band = 0; band < 8; band++) Assert.Equal(1, InterauralCoherence.Iacc(From((cosToLeft, 1)), band), 3);
    }

    [Fact]
    public void TheBandsAutocorrelation_Is1AtNoLag_AndFallsAwayWithinAFewPeriods()
    {
        Assert.Equal(1, InterauralCoherence.BandAutocorrelation(1000, 0), 12);
        Assert.True(InterauralCoherence.BandAutocorrelation(1000, 0.0001) > 0.7);
        Assert.True(Math.Abs(InterauralCoherence.BandAutocorrelation(1000, 0.003)) < 0.1);
    }

    [Fact]
    public void TwoArrivalsFromEitherSide_AreAlikeAtLowFrequencies_AndUnlikeAtHighOnes()
    {
        var both = From((1, 1), (-1, 1));
        Assert.InRange(InterauralCoherence.Iacc(both, 0), 0.85, 1);     // 63 Hz: a delay of 1.3 ms is a twelfth of a period
        Assert.InRange(InterauralCoherence.Iacc(both, 5), 0, 0.6);      // 2 kHz: the shadow and the delay set them apart
    }

    [Fact]
    public void ADiffuseField_IsCoherentAtLowFrequencies_AndHardlyAtAllAtHighOnes()
    {
        var diffuse = Diffuse();
        Assert.InRange(InterauralCoherence.Iacc(diffuse, 1), 0.8, 1);   // 125 Hz
        for (var band = 5; band < 8; band++) Assert.InRange(InterauralCoherence.Iacc(diffuse, band), 0, 0.35);
        // falling with frequency from the low bands to the high ones
        Assert.True(InterauralCoherence.Iacc(diffuse, 2) > InterauralCoherence.Iacc(diffuse, 4));
    }

    [Fact]
    public void AStrongArrivalAheadOverADiffuseField_KeepsTheEarsAlike()
    {
        var diffuse = Diffuse();
        var total = diffuse.Sum();
        var withDirect = (double[])diffuse.Clone();
        withDirect[InterauralCoherence.Cell(0)] += 4 * total;           // the direct sound four times the reverberant energy
        Assert.True(InterauralCoherence.Iacc(withDirect, 5) > 0.6);    // a sound from ahead is quieter at the ears than the field's average, so 0.7 here
        Assert.True(InterauralCoherence.Iacc(withDirect, 5) > InterauralCoherence.Iacc(diffuse, 5));
    }

    [Fact]
    public void NoEnergy_HasNoIacc()
    {
        Assert.True(double.IsNaN(InterauralCoherence.Iacc(new double[InterauralCoherence.Cells], 4)));
    }

    [Fact]
    public void TheCellsRunFromTheRightToTheLeft()
    {
        Assert.Equal(0, InterauralCoherence.Cell(-1));
        Assert.Equal(InterauralCoherence.Cells - 1, InterauralCoherence.Cell(1));
        Assert.Equal(InterauralCoherence.Cells / 2, InterauralCoherence.Cell(1e-9));
        Assert.InRange(InterauralCoherence.CellCosine(InterauralCoherence.Cell(0.5)), 0.49, 0.51);   // within the cell's degree
    }
}

public class InterauralTraceTests
{
    private static SurfaceFinish Finish(double alpha, double scatter) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scatter));

    private static RayTraceOptions Options(bool interaural) => new(RayCount: 20_000, ImageSourceOrder: 2, Interaural: interaural);

    [Fact]
    public void InARoomWithNoReflections_OnlyTheDirectSoundFromAhead_ReachesTheHead_SoTheEarlyIaccIs1()
    {
        var room = Room3D.Shoebox(20, 10, 6, Finish(1, 0));
        var m = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(room, new Vec3(4, 5, 2), new Vec3(12, 5, 2), Options(true)));
        for (var b = 0; b < 8; b++)
        {
            Assert.Equal(1, m.IaccEarly!.Value[b], 3);
            Assert.True(double.IsNaN(m.IaccLate!.Value[b]));        // nothing after 80 ms
            Assert.Equal(1, m.IaccAll!.Value[b], 3);                // the whole response is the direct sound
        }
        var broadband = BroadbandMetrics.From(m);
        Assert.Equal(1, broadband.IaccE3, 3);
        Assert.Equal(0, broadband.BinauralQualityIndex, 3);
    }

    [Fact]
    public void InAReverberantRoom_TheLateSoundFromEveryWay_IsLessAlikeAtTheEarsThanTheEarly()
    {
        var room = Room3D.Shoebox(20, 14, 8, Finish(0.15, 0.5));
        var m = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(room, new Vec3(4, 7, 1.5), new Vec3(9, 7, 1.2), Options(true)));
        for (var b = 4; b < 8; b++)
        {
            Assert.InRange(m.IaccLate!.Value[b], 0, 0.4);
            Assert.True(m.IaccEarly!.Value[b] > m.IaccLate!.Value[b], $"band {b}: early {m.IaccEarly!.Value[b]} over late {m.IaccLate!.Value[b]}");
        }
        Assert.True(m.IaccLate!.Value[1] > m.IaccLate!.Value[6]);   // a diffuse field is more alike at the ears at 125 Hz than at 4 kHz
        var broadband = BroadbandMetrics.From(m);
        Assert.Equal((m.IaccEarly!.Value[3] + m.IaccEarly!.Value[4] + m.IaccEarly!.Value[5]) / 3, broadband.IaccE3, 12);
        Assert.Equal((m.IaccLate!.Value[3] + m.IaccLate!.Value[4] + m.IaccLate!.Value[5]) / 3, broadband.IaccL3, 12);
        Assert.Equal(1 - broadband.IaccE3, broadband.BinauralQualityIndex, 12);
        // the whole response: no more alike at the ears than the more alike of its two parts (the energies of the two windows add, their correlations do not)
        for (var b = 0; b < 8; b++) Assert.True(m.IaccAll!.Value[b] <= Math.Max(m.IaccEarly!.Value[b], m.IaccLate!.Value[b]) + 1e-9);
        Assert.Equal((m.IaccAll!.Value[3] + m.IaccAll!.Value[4] + m.IaccAll!.Value[5]) / 3, broadband.IaccA3, 12);
    }

    [Fact]
    public void GatheringTheDirections_LeavesTheResponseAsItWas_AndWithoutItThereIsNoIacc()
    {
        var room = Room3D.Shoebox(12, 8, 4, Finish(0.2, 0.3));
        var with = EnergyRayTracer.Trace(room, new Vec3(3, 4, 1.5), new Vec3(8, 3, 1.2), Options(true));
        var without = EnergyRayTracer.Trace(room, new Vec3(3, 4, 1.5), new Vec3(8, 3, 1.2), Options(false));
        for (var b = 0; b < 8; b++) Assert.Equal(without.Energy[b], with.Energy[b]);
        Assert.Null(without.Interaural);
        var m = RoomAcousticsCalculator.Calculate(without);
        Assert.Null(m.IaccEarly);
        Assert.True(double.IsNaN(BroadbandMetrics.From(m).IaccE3));
    }

    [Fact]
    public void AnExtendedSource_GathersEachElementsShare()
    {
        var room = Room3D.Shoebox(20, 14, 8, Finish(0.2, 0.3));
        var line = new ExtendedSource(new[] { new SourcePoint(new Vec3(4, 6, 1.5), 1), new SourcePoint(new Vec3(4, 8, 1.5), 1) });
        var r = ExtendedSourceTracer.TraceReceivers(room, line, new[] { new Vec3(12, 7, 1.2) }, Options(true))[0];
        Assert.NotNull(r.Interaural);
        var m = RoomAcousticsCalculator.Calculate(r);
        Assert.InRange(m.IaccEarly!.Value[4], 0, 1);
    }
}

public class ListenIaccTests
{
    private static Room3D Room(double alpha) =>
        Room3D.Shoebox(20, 14, 8, new SurfaceFinish(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0.4)));

    private static RayTraceOptions Quick() => new(RayCount: 20_000, ImageSourceOrder: 2, MaxTimeS: 1.2);

    [Theory]
    [InlineData(180)]    // facing the source
    [InlineData(90)]     // the source to the right
    public void WithNoReflections_TheHeadHearsOneArrival_WhicheverWayItFaces_SoTheEarlyIaccIs1(double facingDeg)
    {
        var heard = RoomAuralisation.Render(Room(1), ExtendedSource.Point(new Vec3(4, 7, 1.5)), new Vec3(12, 7, 1.5), facingDeg, new[] { 1.0 }, 22_050, Quick());
        var early = InterauralCoherence.Bands(heard.Head!.Early);
        for (var b = 0; b < 8; b++) Assert.Equal(1, early[b], 3);
    }

    [Fact]
    public void InAReverberantRoom_TheLateSoundIsLessAlikeAtTheEars_ExceptAtLowFrequencies()
    {
        var room = Room(0.15);
        var source = ExtendedSource.Point(new Vec3(4, 7, 1.5));
        var heard = RoomAuralisation.Render(room, source, new Vec3(10, 7, 1.2), 180, new[] { 1.0 }, 22_050, Quick());
        var early = InterauralCoherence.Bands(heard.Head!.Early);
        var late = InterauralCoherence.Bands(heard.Head.Late);
        Assert.True(late[0] > 0.9, "63 Hz is alike at two ears a head apart");
        Assert.True(early[5] > late[5] && late[5] < 0.4, $"2 kHz: early {early[5]}, late {late[5]}");
    }
}
