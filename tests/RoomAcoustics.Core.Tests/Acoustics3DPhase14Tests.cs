using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class HeadShadowTests
{
    [Fact]
    public void TheGain_MatchesTheBrownAndDudaFormula_AtKnownPoints()
    {
        // 4 kHz (band 6), sound arriving at right angles to the ear: theta = 90 degrees.
        var alpha = 1.05 + 0.95 * Math.Cos(90 * Math.PI / 150);
        var x = Math.PI * 4000 * 0.0875 / 343;
        var expected = (1 + alpha * alpha * x * x) / (1 + x * x);

        Assert.Equal(expected, HeadShadow.PowerGain(6, 0), 9);
        Assert.Equal(10 * Math.Log10(expected), HeadShadow.GainDb(6, 0), 9);
    }

    [Fact]
    public void TheEarFacingTheSound_HearsMore_AndTheFarEarMuchLess_AtHighFrequencies()
    {
        Assert.True(HeadShadow.GainDb(7, 1) > 3, $"{HeadShadow.GainDb(7, 1):0.0} dB");
        Assert.True(HeadShadow.GainDb(7, -1) < -9, $"{HeadShadow.GainDb(7, -1):0.0} dB");
        Assert.True(HeadShadow.GainDb(7, 1) - HeadShadow.GainDb(7, -1) > 14);
    }

    [Fact]
    public void ALowFrequency_PassesRoundTheHead_FromEveryDirection()
    {
        // The head is small against a 63 Hz wavelength, so there is almost no shadow.
        foreach (var cos in new[] { -1.0, -0.5, 0, 0.5, 1 })
            Assert.InRange(HeadShadow.GainDb(0, cos), -0.1, 0.1);
    }

    [Fact]
    public void TheShadowDeepens_AsTheSoundMovesRoundToTheFarSide()
    {
        // Angles of 0, 90 and 150 degrees from the ear's axis.
        var toward = HeadShadow.PowerGain(6, 1);
        var across = HeadShadow.PowerGain(6, 0);
        var behind = HeadShadow.PowerGain(6, -Math.Cos(30 * Math.PI / 180));

        Assert.True(toward > across && across > behind);
    }

    [Fact]
    public void FromAllDirectionsAtOnce_TheEarHearsAboutAsMuchAsWithoutAHead()
    {
        // A diffuse field arrives equally from every direction: cos theta is uniform on -1 to 1. The head should neither add nor
        // remove much power on average.
        for (var band = 0; band < 8; band++)
        {
            var mean = Enumerable.Range(0, 2000).Average(i => HeadShadow.PowerGain(band, -1 + (i + 0.5) / 1000));
            Assert.InRange(10 * Math.Log10(mean), -2.5, 2.5);
        }
    }

    [Fact]
    public void ABandOutOfRange_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadShadow.PowerGain(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadShadow.PowerGain(8, 0));
    }
}

public class HeadShadowTraceTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    private static Room3D DeadRoom() => Room3D.Shoebox(8, 6, 3, Finish(0.95));

    private static readonly Vec3 Source = new(1, 3, 1.5);
    private static readonly Vec3 Ear = new(5, 3, 1.5);

    private static RayTraceOptions Quick() => new(RayCount: 20_000, ImageSourceOrder: 1);

    private static double Ratio(ImpulseResponse r, int band) => r.ShadowedEnergy![band].Sum() / r.Energy[band].Sum();

    [Fact]
    public void WithoutEarAxes_NoShadowedEnergyIsTraced()
    {
        var response = EnergyRayTracer.TraceReceivers(DeadRoom(), Source, new[] { Ear }, Quick())[0];

        Assert.Null(response.ShadowedEnergy);
    }

    [Fact]
    public void AnEarFacingTheSource_IsBoostedAtHighFrequencies_AndOneFacingAwayIsShaded()
    {
        var room = DeadRoom();
        var towards = EnergyRayTracer.TraceReceivers(room, Source, new[] { Ear }, Quick(), new[] { new Vec3(-1, 0, 0) })[0];
        var away = EnergyRayTracer.TraceReceivers(room, Source, new[] { Ear }, Quick(), new[] { new Vec3(1, 0, 0) })[0];

        Assert.True(Ratio(towards, 7) > 2, $"facing the source {Ratio(towards, 7):0.00}");
        Assert.True(Ratio(away, 7) < 0.15, $"facing away {Ratio(away, 7):0.00}");
        // The unshadowed energy is the same either way.
        Assert.Equal(towards.Energy[7], away.Energy[7]);
    }

    [Fact]
    public void ALowBand_IsHardlyChangedByTheShadow()
    {
        var away = EnergyRayTracer.TraceReceivers(DeadRoom(), Source, new[] { Ear }, Quick(), new[] { new Vec3(1, 0, 0) })[0];

        Assert.InRange(Ratio(away, 0), 0.97, 1.03);
    }

    [Fact]
    public void ExtendedSources_CarryTheShadowedEnergyToo_AndAPointSourceMatches()
    {
        var room = DeadRoom();
        var normals = new[] { new Vec3(1, 0, 0) };
        var line = ExtendedSource.Line(new Vec3(1, 2, 1.5), new Vec3(1, 4, 1.5), 3);

        var lineResponse = ExtendedSourceTracer.TraceReceivers(room, line, new[] { Ear }, Quick(), normals)[0];
        var single = ExtendedSourceTracer.TraceReceivers(room, ExtendedSource.Point(Source), new[] { Ear }, Quick(), normals)[0];
        var direct = EnergyRayTracer.TraceReceivers(room, Source, new[] { Ear }, Quick(), normals)[0];

        Assert.NotNull(lineResponse.ShadowedEnergy);
        Assert.Equal(direct.ShadowedEnergy![7], single.ShadowedEnergy![7]);
    }

    [Fact]
    public void OneEarAxisIsNeededForEachReceiver()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            EnergyRayTracer.TraceReceivers(DeadRoom(), Source, new[] { Ear }, Quick(), new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0) }));

        Assert.Contains("one ear axis", ex.Message);
    }
}

public class HeadShadowAuralisationTests
{
    private const int Rate = 22050;

    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    private static Room3D DeadRoom() => Room3D.Shoebox(6, 4, 3, Finish(0.8));

    private static RayTraceOptions Quick() => new(RayCount: 20_000, ImageSourceOrder: 1);

    [Fact]
    public void ASourceOnTheListenersRight_IsMuchLouderInTheRightEar_WithTheHeadShadowThanWithout()
    {
        // Facing +x the right-hand side is -y. The source is 1.5 m out to the right. Bare ears 17 cm apart differ by under 1 dB; the
        // head's shadow on the left ear adds several dB at the high frequencies that carry most of a click's energy.
        var room = DeadRoom();
        var source = ExtendedSource.Point(new Vec3(4, 0.5, 1.2));
        var head = new Vec3(4, 2, 1.2);
        var dry = DrySignals.ClickTrain(Rate, 0.6);

        double Mean(bool shadow) => Enumerable.Range(1, 3).Average(seed =>
            RoomAuralisation.Render(room, source, head, 0, dry, Rate, Quick() with { Seed = seed }, headShadow: shadow).InterauralLevelDifferenceDb);

        var bare = Mean(false);
        var shaded = Mean(true);

        Assert.True(bare < 0, $"bare ears {bare:0.0} dB");
        Assert.True(shaded < bare - 3, $"bare {bare:0.0} dB, with head shadow {shaded:0.0} dB");
    }

    [Fact]
    public void ASourceStraightAhead_IsHeardAboutEquallyByBothEars_WithTheHeadShadow()
    {
        var source = ExtendedSource.Point(new Vec3(1, 2, 1.2));

        var result = RoomAuralisation.Render(DeadRoom(), source, new Vec3(4, 2, 1.2), 180, DrySignals.ClickTrain(Rate, 0.6), Rate, Quick());

        Assert.InRange(result.InterauralLevelDifferenceDb, -2, 2);
    }

    [Fact]
    public void TheHeadShadow_IsOnByDefault_AndRepeatable()
    {
        var room = DeadRoom();
        var source = ExtendedSource.Point(new Vec3(4, 0.5, 1.2));
        var head = new Vec3(4, 2, 1.2);
        var dry = DrySignals.ClickTrain(Rate, 0.6);

        var byDefault = RoomAuralisation.Render(room, source, head, 0, dry, Rate, Quick());
        var explicitOn = RoomAuralisation.Render(room, source, head, 0, dry, Rate, Quick(), headShadow: true);
        var off = RoomAuralisation.Render(room, source, head, 0, dry, Rate, Quick(), headShadow: false);

        Assert.Equal(byDefault.Left, explicitOn.Left);
        Assert.Equal(byDefault.Right, explicitOn.Right);
        Assert.NotEqual(byDefault.Left, off.Left);
    }
}

public class HeadDelayTests
{
    private const int Rate = 22050;
    private const double Speed = 331.3 + 0.606 * 20;

    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    private static Room3D DeadRoom() => Room3D.Shoebox(6, 4, 3, Finish(0.8));

    private static RayTraceOptions Quick() => new(RayCount: 20_000, ImageSourceOrder: 1);

    // The listener faces +x, so the right-hand side is -y; this source is 1.7 m directly to the right.
    private static readonly ExtendedSource OnTheRight = ExtendedSource.Point(new Vec3(4, 0.3, 1.2));
    private static readonly Vec3 Head = new(4, 2, 1.2);

    [Fact]
    public void ThePathRoundTheHead_IsStraightToTheFacingEar_AndRoundTheSurfaceToTheOther()
    {
        var a = HeadShadow.HeadRadiusM;

        Assert.Equal(-a, HeadShadow.PathAroundHeadM(1), 12);
        Assert.Equal(0, HeadShadow.PathAroundHeadM(0), 12);
        Assert.Equal(a * Math.PI / 2, HeadShadow.PathAroundHeadM(-1), 12);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public void TheDifferenceBetweenTheEars_IsWoodworthsFormula_AndAtMostAboutSixTenthsOfAMillisecond(double azimuthDeg)
    {
        // A source azimuthDeg from straight ahead: the near ear's axis is (90 - azimuth) from the sound, the far ear's (90 + azimuth).
        var phi = azimuthDeg * Math.PI / 180;
        var near = HeadShadow.PathAroundHeadM(Math.Cos(Math.PI / 2 - phi));
        var far = HeadShadow.PathAroundHeadM(Math.Cos(Math.PI / 2 + phi));

        Assert.Equal(HeadShadow.HeadRadiusM * (phi + Math.Sin(phi)), far - near, 12);
        Assert.InRange((far - near) / Speed * 1000, 0, 0.66);
    }

    [Fact]
    public void ASourceOnTheRight_ReachesTheRightEarFirst_ByTheWoodworthDifference()
    {
        var result = RoomAuralisation.Render(DeadRoom(), OnTheRight, Head, 0, DrySignals.ClickTrain(Rate, 0.6), Rate, Quick());

        var expected = -HeadShadow.HeadRadiusM * (Math.PI / 2 + 1) / Speed * 1000;
        Assert.Equal(expected, result.InterauralTimeDifferenceMs, 3);
        Assert.InRange(result.InterauralTimeDifferenceMs, -0.7, -0.6);
    }

    [Fact]
    public void ASourceDeadAhead_HasNoTimeDifference()
    {
        var result = RoomAuralisation.Render(DeadRoom(), ExtendedSource.Point(new Vec3(1, 2, 1.2)), Head, 180,
            DrySignals.ClickTrain(Rate, 0.6), Rate, Quick());

        Assert.Equal(0, result.InterauralTimeDifferenceMs, 6);
    }

    [Fact]
    public void WithTheHeadDelayOff_NoTimeDifferenceIsReported_AndTheResponsesAreNotMoved()
    {
        var dry = DrySignals.ClickTrain(Rate, 0.6);

        var off = RoomAuralisation.Render(DeadRoom(), OnTheRight, Head, 0, dry, Rate, Quick(), headDelay: false);
        var on = RoomAuralisation.Render(DeadRoom(), OnTheRight, Head, 0, dry, Rate, Quick());

        Assert.Equal(0, off.InterauralTimeDifferenceMs);
        Assert.Equal(off.Left.Length, off.Right.Length);
        Assert.Equal(on.Left.Length, on.Right.Length);
        Assert.True(on.Left.Length >= off.Left.Length);
    }

    [Fact]
    public void TheEarsResponses_AreMovedSoThatTheDirectSoundsFallWhereTheModelPutsThem()
    {
        var dry = DrySignals.ClickTrain(Rate, 0.6);
        var off = RoomAuralisation.Render(DeadRoom(), OnTheRight, Head, 0, dry, Rate, Quick(), headDelay: false);
        var on = RoomAuralisation.Render(DeadRoom(), OnTheRight, Head, 0, dry, Rate, Quick());

        // The same seed gives the same noise, so each channel with the delay on is the one with it off, moved later by whole samples.
        var leftShift = ShiftOf(off.Left, on.Left);
        var rightShift = ShiftOf(off.Right, on.Right);

        // Without the move each ear's direct sound sits in the middle of its 1 ms bin, whatever the true arrival; with it, the arrival
        // times differ by the model's interaural difference.
        double BinCentre(Vec3 ear) => (Math.Floor((ear - OnTheRight.Centre).Length / Speed / 0.001) + 0.5) * 0.001;
        var realisedMs = ((rightShift - leftShift) / (double)Rate + BinCentre(on.RightEar) - BinCentre(on.LeftEar)) * 1000;

        // Whole samples (0.045 ms each) limit how closely the model's figure can be met.
        Assert.InRange(realisedMs, on.InterauralTimeDifferenceMs - 0.05, on.InterauralTimeDifferenceMs + 0.05);
    }

    // The number of samples that the second signal is later than the first, found by matching them.
    private static int ShiftOf(double[] original, double[] moved)
    {
        for (var shift = 0; shift < 400; shift++)
        {
            var worst = 0.0;
            for (var i = 0; i < original.Length && worst < 1e-6; i++)
                worst = Math.Max(worst, Math.Abs(moved[i + shift] - original[i]));
            if (worst < 1e-6) return shift;
        }
        throw new Xunit.Sdk.XunitException("The delayed signal is not a moved copy of the original.");
    }
}
