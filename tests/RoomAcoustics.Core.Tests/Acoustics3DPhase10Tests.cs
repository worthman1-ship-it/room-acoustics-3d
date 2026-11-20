using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class PolarDirectivityTests
{
    private static readonly double[] Angles = { 0, 45, 90, 135, 180 };

    /// <summary>A pattern that is flat at 63 Hz and falls 12 dB to the back at 8 kHz, in between in the other bands.</summary>
    private static PolarDirectivity Speaker()
    {
        var bands = Enumerable.Range(0, 8).Select(b =>
            (IReadOnlyList<double>)Angles.Select(a => -(b / 7.0) * 12 * (a / 180)).ToArray()).ToArray();
        return new PolarDirectivity(Angles, bands);
    }

    private static double MeanGain(SourceDirectivity d)
    {
        const int n = 50_000;
        var golden = Math.PI * (3 - Math.Sqrt(5));
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var z = 1 - 2 * (i + 0.37) / n; // a different lattice from the one the class normalises on
            var r = Math.Sqrt(1 - z * z);
            sum += d.Gain(new Vec3(r * Math.Cos(i * golden), r * Math.Sin(i * golden), z));
        }
        return sum / n;
    }

    [Fact]
    public void TheLevel_IsInterpolatedInDecibels_AndHeldBeyondTheEnds()
    {
        var polar = new PolarDirectivity(new double[] { 0, 90, 180 },
            Enumerable.Repeat((IReadOnlyList<double>)new double[] { 0, -6, -18 }, 8).ToArray());

        Assert.Equal(-3, polar.LevelDb(3, 45), 9);
        Assert.Equal(-12, polar.LevelDb(3, 135), 9);
        Assert.Equal(0, polar.LevelDb(3, 0), 9);
        Assert.Equal(-18, polar.LevelDb(3, 180), 9);

        var short_ = new PolarDirectivity(new double[] { 30, 150 }, Enumerable.Repeat((IReadOnlyList<double>)new double[] { 2, -4 }, 8).ToArray());
        Assert.Equal(2, short_.LevelDb(0, 0), 9);
        Assert.Equal(-4, short_.LevelDb(0, 180), 9);
    }

    [Fact]
    public void Aimed_ReproducesTheLevelsOnAndOffAxis_InEachBand()
    {
        var aimed = Speaker().Aimed(new Vec3(1, 0, 0));

        for (var b = 0; b < 8; b++)
        {
            var d = aimed.ForBand(b);
            var expected = Math.Pow(10, Speaker().LevelDb(b, 180) / 10); // straight behind, against on axis
            Assert.Equal(expected, d.Gain(new Vec3(-1, 0, 0)) / d.Gain(new Vec3(1, 0, 0)), 9);
        }
    }

    [Fact]
    public void EveryBandsPattern_RadiatesTheSamePower()
    {
        var aimed = Speaker().Aimed(new Vec3(0, 1, 0));

        for (var b = 0; b < 8; b++) Assert.Equal(1, MeanGain(aimed.ForBand(b)), 2);
    }

    [Fact]
    public void ThePatternIsNarrowerInTheHighBands_AndFollowsTheAim()
    {
        var towardsX = Speaker().Aimed(new Vec3(1, 0, 0));
        var towardsY = Speaker().Aimed(new Vec3(0, 1, 0));
        var x = new Vec3(1, 0, 0);

        Assert.Equal(1, towardsX.ForBand(0).Gain(x), 2); // flat at 63 Hz
        Assert.True(towardsX.ForBand(7).Gain(x) > 1.5);
        Assert.True(towardsX.ForBand(7).Gain(x) > towardsY.ForBand(7).Gain(x) * 1.5);
    }

    [Fact]
    public void ASourceWithoutBands_HasTheSamePatternInEveryBand_AndABandedOneAnswersBandIndexRange()
    {
        var cardioid = SourceDirectivity.Cardioid(new Vec3(1, 0, 0));
        Assert.Same(cardioid, cardioid.ForBand(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => cardioid.ForBand(8));
        Assert.Throws<ArgumentException>(() => SourceDirectivity.PerBand(new[] { cardioid }));
    }

    [Fact]
    public void TheTracer_AppliesEachBandsPattern_ToTheDirectSound()
    {
        var absorber = new SurfaceFinish(new AbsorptionMaterial("black", "User", Enumerable.Repeat((double?)1.0, 8).ToArray()), OctaveSpectrum.Flat(0));
        var room = Room3D.Shoebox(20, 10, 4, absorber);
        var source = new Vec3(10, 5, 2);
        var front = new Vec3(13, 5, 2);
        var back = new Vec3(7, 5, 2);
        var options = new RayTraceOptions(RayCount: 100, Directivity: Speaker().Aimed(new Vec3(1, 0, 0)));

        var responses = EnergyRayTracer.TraceReceivers(room, source, new[] { front, back }, options);

        foreach (var b in new[] { 0, 3, 7 })
        {
            var ratio = responses[1].Energy[b].Sum() / responses[0].Energy[b].Sum();
            Assert.Equal(Math.Pow(10, Speaker().LevelDb(b, 180) / 10), ratio, 9);
        }
    }

    [Fact]
    public void ACsvTable_WithEightBands_IsRead_InAnyColumnOrderAndWithComments()
    {
        var csv = string.Join("\n", "# a made-up loudspeaker", "angle_deg;8000 Hz;63;125;250;500;1000;2000;4000",
            "0;0;0;0;0;0;0;0;0", "", "90;-9;-1;-2;-3;-4;-5;-6;-7", "180;-18;-2;-4;-6;-8;-10;-12;-14");

        var polar = PolarDirectivity.FromCsv(csv);

        Assert.Equal(new double[] { 0, 90, 180 }, polar.AnglesDeg);
        Assert.Equal(-1, polar.LevelDb(0, 90), 9);   // 63 Hz
        Assert.Equal(-9, polar.LevelDb(7, 90), 9);   // 8 kHz
        Assert.Equal(-5, polar.LevelDb(4, 90), 9);   // 1 kHz
    }

    [Fact]
    public void ACsvTable_WithOneColumn_AppliesToEveryBand()
    {
        var polar = PolarDirectivity.FromCsv("angle_deg,level_db\r\n0,0\r\n180,-10\r\n");

        for (var b = 0; b < 8; b++) Assert.Equal(-5, polar.LevelDb(b, 90), 9);
    }

    [Fact]
    public void TheExampleInTheReadme_IsAValidTable()
    {
        var csv = string.Join("\n", "angle_deg,63,125,250,500,1000,2000,4000,8000",
            "0,0,0,0,0,0,0,0,0", "45,0,0,-0.5,-1,-2,-3,-5,-8", "90,-0.5,-1,-2,-4,-6,-9,-13,-18",
            "135,-1,-2,-4,-7,-10,-14,-19,-26", "180,-1.5,-3,-5,-9,-13,-18,-24,-32");

        var polar = PolarDirectivity.FromCsv(csv);

        Assert.Equal(5, polar.AnglesDeg.Count);
        Assert.Equal(-18, polar.LevelDb(7, 90), 9);
        Assert.True(polar.Aimed(new Vec3(0, 0, 1)).ForBand(7).Gain(new Vec3(0, 0, 1)) > 3); // narrow at 8 kHz
    }

    [Theory]
    [InlineData("angle_deg,63,125\n0,0,0\n90,-1,-1")]                      // neither one column nor eight
    [InlineData("angle_deg,63,125,250,500,1000,2000,4000,4000\n0,0,0,0,0,0,0,0,0\n90,0,0,0,0,0,0,0,0")] // a band twice
    [InlineData("angle_deg,63,125,250,500,1000,2000,4000,9000\n0,0,0,0,0,0,0,0,0\n90,0,0,0,0,0,0,0,0")] // not an octave band
    [InlineData("angle_deg,level\n0,0\n90,x")]                              // not a number
    [InlineData("angle_deg,level\n0,0\n90")]                                // a short line
    [InlineData("angle_deg,level\n0,0")]                                    // one angle
    [InlineData("angle_deg,level\n90,0\n0,-3")]                             // angles out of order
    [InlineData("angle_deg,level\n0,0\n200,-3")]                            // beyond 180 degrees
    public void ABadTable_IsRefusedWithAMessage(string csv)
    {
        var ex = Assert.Throws<FormatException>(() => PolarDirectivity.FromCsv(csv));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void TheConstructor_ChecksItsArguments()
    {
        var eight = Enumerable.Repeat((IReadOnlyList<double>)new double[] { 0, -1 }, 8).ToArray();
        Assert.Throws<ArgumentException>(() => new PolarDirectivity(new double[] { 0 }, eight));
        Assert.Throws<ArgumentException>(() => new PolarDirectivity(new double[] { 0, -5 }, eight));
        Assert.Throws<ArgumentException>(() => new PolarDirectivity(new double[] { 0, 90 }, eight.Take(7).ToArray()));
        Assert.Throws<ArgumentException>(() => new PolarDirectivity(new double[] { 0, 90, 180 }, eight));
        Assert.Throws<ArgumentException>(() => new PolarDirectivity(new double[] { 0, 90 },
            Enumerable.Repeat((IReadOnlyList<double>)new[] { 0, double.NaN }, 8).ToArray()));
    }
}

public class ExtendedSourceTests
{
    private static readonly SurfaceFinish Absorber =
        new(new AbsorptionMaterial("black", "User", Enumerable.Repeat((double?)1.0, 8).ToArray()), OctaveSpectrum.Flat(0));

    private static SurfaceFinish Finish(double alpha, double scattering = 0) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(scattering));

    private static double Total(ImpulseResponse r, int band = 4) => r.Energy[band].Sum();

    /// <summary>The direct energy at a receiver sphere from a point at a distance, as the tracer works it out (1 kHz).</summary>
    private static double Direct(double distance, double radius = 0.5) =>
        (1 - Math.Sqrt(1 - (radius / distance) * (radius / distance))) / 2 *
        Math.Pow(10, -AirAbsorption.AttenuationDbPerMetre(1000, AirAbsorption.ReferencePressurePa, 20, 60) * distance / 10);

    [Fact]
    public void ALine_HasItsElementsAtTheMiddlesOfEqualParts()
    {
        var line = ExtendedSource.Line(new Vec3(0, 0, 1), new Vec3(4, 0, 1), 4);

        Assert.Equal(new[] { 0.5, 1.5, 2.5, 3.5 }, line.Points.Select(p => p.Position.X));
        Assert.All(line.Points, p => Assert.Equal(1, p.Weight));
        Assert.Equal(2, line.Centre.X, 9);
        Assert.False(line.IsPoint);
    }

    [Fact]
    public void ARectangle_IsCentredAndSpansItsWidthAndHeight()
    {
        var plate = ExtendedSource.Rectangle(new Vec3(5, 5, 2), new Vec3(0, 1, 0), new Vec3(0, 0, 1), 2, 1, 2, 2);

        Assert.Equal(4, plate.Points.Count);
        Assert.Equal(new Vec3(5, 5, 2).X, plate.Centre.X, 9);
        Assert.Equal(2, plate.Centre.Z, 9);
        Assert.Equal(new[] { 4.5, 5.5 }, plate.Points.Select(p => p.Position.Y).Distinct().OrderBy(y => y));
        Assert.Equal(new[] { 1.75, 2.25 }, plate.Points.Select(p => p.Position.Z).Distinct().OrderBy(z => z));
    }

    [Fact]
    public void TheCentre_IsWeighted()
    {
        var source = new ExtendedSource(new[] { new SourcePoint(new Vec3(0, 0, 0), 3), new SourcePoint(new Vec3(4, 0, 0), 1) });

        Assert.Equal(1, source.Centre.X, 9);
    }

    [Fact]
    public void ABadSource_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ExtendedSource(Array.Empty<SourcePoint>()));
        Assert.Throws<ArgumentException>(() => new ExtendedSource(new[] { new SourcePoint(Vec3.Zero, 0) }));
        Assert.Throws<ArgumentException>(() => new ExtendedSource(new[] { new SourcePoint(Vec3.Zero, -1) }));
        Assert.Throws<ArgumentException>(() => ExtendedSource.Line(Vec3.Zero, new Vec3(1, 0, 0), ExtendedSource.MaxPoints + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtendedSource.Line(Vec3.Zero, new Vec3(1, 0, 0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtendedSource.Rectangle(Vec3.Zero, new Vec3(1, 0, 0), new Vec3(0, 0, 1), 0, 1, 1, 1));
    }

    [Fact]
    public void ASingleElement_GivesExactlyThePointSourceResult()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, 0.3));
        var source = new Vec3(2, 2, 1.5);
        var receivers = new[] { new Vec3(6, 4, 1.2), new Vec3(8, 5, 1.2) };
        var options = new RayTraceOptions(RayCount: 5000, ImageSourceOrder: 2);

        var point = EnergyRayTracer.TraceReceivers(room, source, receivers, options);
        var extended = ExtendedSourceTracer.TraceReceivers(room, ExtendedSource.Point(source), receivers, options);

        for (var r = 0; r < receivers.Length; r++)
            for (var b = 0; b < 8; b++)
            {
                Assert.Equal(point[r].Energy[b], extended[r].Energy[b]);
                Assert.Equal(point[r].LateralEnergy![b], extended[r].LateralEnergy![b]);
            }
    }

    [Fact]
    public void TheDirectSoundOfALine_IsTheSumOverItsElements()
    {
        var room = Room3D.Shoebox(100, 40, 10, Absorber);
        var line = ExtendedSource.Line(new Vec3(40, 20, 2), new Vec3(60, 20, 2), 40);
        var receiver = new Vec3(50, 23, 2);

        var response = ExtendedSourceTracer.TraceReceivers(room, line, new[] { receiver }, new RayTraceOptions(RayCount: 400))[0];

        var expected = line.Points.Sum(p => Direct((receiver - p.Position).Length) / 40);
        Assert.Equal(expected, Total(response), 12);
    }

    [Fact]
    public void ALineFallsBy3dBPerDoublingNearIt_WhereAPointFallsBy6()
    {
        var room = Room3D.Shoebox(100, 40, 10, Absorber);
        var line = ExtendedSource.Line(new Vec3(40, 20, 2), new Vec3(60, 20, 2), 40);
        var near = new Vec3(50, 22, 2);
        var far = new Vec3(50, 24, 2);
        var options = new RayTraceOptions(RayCount: 400);

        var responses = ExtendedSourceTracer.TraceReceivers(room, line, new[] { near, far }, options);
        var lineDrop = 10 * Math.Log10(Total(responses[0]) / Total(responses[1]));

        var pointNear = EnergyRayTracer.Trace(room, new Vec3(50, 20, 2), near, options);
        var pointFar = EnergyRayTracer.Trace(room, new Vec3(50, 20, 2), far, options);
        var pointDrop = 10 * Math.Log10(Total(pointNear) / Total(pointFar));

        Assert.InRange(lineDrop, 3.3, 4.0); // 3 dB for an infinite line, a little more at 20 m long
        Assert.InRange(pointDrop, 5.8, 6.3);
    }

    [Fact]
    public void ElementsAddInProportionToTheirPower()
    {
        var room = Room3D.Shoebox(100, 40, 10, Absorber);
        var a = new Vec3(40, 20, 2);
        var b = new Vec3(60, 25, 2);
        var receiver = new Vec3(50, 22, 2);
        var options = new RayTraceOptions(RayCount: 400);

        var mixed = ExtendedSourceTracer.TraceReceivers(room, new ExtendedSource(new[] { new SourcePoint(a, 3), new SourcePoint(b, 1) }),
            new[] { receiver }, options)[0];
        var onlyA = EnergyRayTracer.Trace(room, a, receiver, options);
        var onlyB = EnergyRayTracer.Trace(room, b, receiver, options);

        Assert.Equal(0.75 * Total(onlyA) + 0.25 * Total(onlyB), Total(mixed), 12);
    }

    [Fact]
    public void AnElementsOwnDirectivity_ReplacesTheOptionsOne()
    {
        var room = Room3D.Shoebox(100, 40, 10, Absorber);
        var position = new Vec3(50, 20, 2);
        var receiver = new Vec3(53, 20, 2);
        var cardioid = SourceDirectivity.Cardioid(new Vec3(1, 0, 0));
        var options = new RayTraceOptions(RayCount: 100);

        var omni = ExtendedSourceTracer.TraceReceivers(room, new ExtendedSource(new[] { new SourcePoint(position), new SourcePoint(position + new Vec3(0, 1, 0)) }),
            new[] { receiver }, options)[0];
        var aimed = ExtendedSourceTracer.TraceReceivers(room,
            new ExtendedSource(new[] { new SourcePoint(position, 1, cardioid), new SourcePoint(position + new Vec3(0, 1, 0), 1, cardioid) }),
            new[] { receiver }, options)[0];

        Assert.True(Total(aimed) > Total(omni) * 1.5); // on the cardioid's axis, the front gain is about 2
    }

    [Fact]
    public void AnExtendedSource_GivesAMapWithResponsesAndMetrics()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2, 0.3));
        var line = ExtendedSource.Line(new Vec3(2, 2, 1.5), new Vec3(2, 5, 1.5), 3);
        var receivers = new[] { new Vec3(6, 4, 1.2), new Vec3(8, 5, 1.2) };

        var map = AcousticMap.Calculate(room, line, receivers, new RayTraceOptions(RayCount: 6000, ImageSourceOrder: 1));

        Assert.Equal(2, map.Metrics.Count);
        Assert.Equal(2, map.Responses!.Count);
        Assert.All(map.Values(m => m.T30S, 1000), t => Assert.InRange(t, 0.2, 3));
    }

    [Fact]
    public void AnElementInsideAReceiverSphere_IsRefused()
    {
        var room = Room3D.Shoebox(10, 7, 3, Finish(0.2));
        var line = ExtendedSource.Line(new Vec3(2, 2, 1.5), new Vec3(6, 2, 1.5), 4);

        Assert.Throws<ArgumentException>(() => ExtendedSourceTracer.TraceReceivers(room, line, new[] { new Vec3(5.2, 2.1, 1.5) }));
    }
}
