using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class AimedLineTests
{
    private static SurfaceFinish Finish(double alpha) =>
        new(new AbsorptionMaterial("test", "User", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    [Fact]
    public void TheElements_AreWhereALineWouldPutThem_EachWithItsOwnPattern()
    {
        var from = new Vec3(2, 2, 0);
        var to = new Vec3(2, 2, 2);
        var patterns = new[] { SourceDirectivity.Cardioid(new Vec3(1, 0, -1)), null, SourceDirectivity.Cardioid(new Vec3(1, 0, 1)) };

        var aimed = ExtendedSource.AimedLine(from, to, 3, i => patterns[i]);
        var plain = ExtendedSource.Line(from, to, 3);

        Assert.Equal(plain.Points.Select(p => p.Position), aimed.Points.Select(p => p.Position));
        Assert.Equal(patterns, aimed.Points.Select(p => p.Directivity));
        Assert.All(aimed.Points, p => Assert.Equal(1, p.Weight));
    }

    [Fact]
    public void ABadCountOrMissingFunction_IsRefused()
    {
        var from = new Vec3(0, 0, 0);
        var to = new Vec3(0, 0, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => ExtendedSource.AimedLine(from, to, 0, _ => null));
        Assert.Throws<ArgumentNullException>(() => ExtendedSource.AimedLine(from, to, 2, null!));
    }

    [Fact]
    public void ElementsAimedApart_SendLessSoundStraightAhead_ThanElementsAimedTheSameWay()
    {
        // Two cardioids 0.4 m apart, a long way from a receiver straight in front of them in a nearly dead room, so that the direct
        // sound is almost all there is. Aimed along +x both give twice the average level on that axis; aimed 90 degrees up and 90
        // degrees down each gives one: half the energy.
        var room = Room3D.Shoebox(12, 4, 3, Finish(0.99));
        var receiver = new[] { new Vec3(9, 2, 1.2) };
        var options = new RayTraceOptions(RayCount: 2_000, ImageSourceOrder: 0);

        double Energy(ExtendedSource source) =>
            ExtendedSourceTracer.TraceReceivers(room, source, receiver, options)[0].Energy[4].Sum();

        var low = new Vec3(2, 2, 1.0);
        var high = new Vec3(2, 2, 1.4);
        var together = Energy(ExtendedSource.AimedLine(low, high, 2, _ => SourceDirectivity.Cardioid(new Vec3(1, 0, 0))));
        var apart = Energy(ExtendedSource.AimedLine(low, high, 2, i => SourceDirectivity.Cardioid(new Vec3(0, 0, i == 0 ? -1 : 1))));

        Assert.InRange(apart / together, 0.45, 0.58);
    }
}
