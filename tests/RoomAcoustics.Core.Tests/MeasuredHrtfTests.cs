using PureHDF;
using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class MeasuredHrtfTests
{
    /// <summary>
    /// A SOFA file of a made-up head measured every 30 degrees of azimuth at elevations -30 to 90: each ear's response a single click, louder from its own
    /// side (1 ± 0.8 sin az cos el), and above 2 kHz softer from behind than in front for both ears.
    /// </summary>
    internal static byte[] Sofa(bool swapEars = false, string positionType = "spherical")
    {
        const int n = 256, rate = 48_000;
        var positions = new List<double[]>();
        for (var el = -30; el <= 90; el += 30)
            for (var az = 0; az < 360; az += el == 90 ? 360 : 30)
                positions.Add(new double[] { az, el, 1.5 });
        var m = positions.Count;
        var ir = new double[m, 2, n];
        for (var i = 0; i < m; i++)
        {
            double az = positions[i][0] * Math.PI / 180, el = positions[i][1] * Math.PI / 180;
            var side = Math.Sin(az) * Math.Cos(el);
            double left = 1 + 0.8 * side, right = 1 - 0.8 * side;
            var behind = Math.Cos(az) * Math.Cos(el) < -0.5;
            for (var ear = 0; ear < 2; ear++)
            {
                var level = (ear == 0) != swapEars ? left : right;
                ir[i, ear, 10] = level;
                // from behind, the click spread over four samples: a low-pass that takes about 7 dB off at 8 kHz and nothing at the lows
                if (behind) for (var k = 0; k < 4; k++) ir[i, ear, 10 + k] = level / 4;
            }
        }
        var position = new double[m, 3];
        for (var i = 0; i < m; i++)
        {
            if (positionType == "cartesian")
            {
                double az = positions[i][0] * Math.PI / 180, el = positions[i][1] * Math.PI / 180;
                position[i, 0] = Math.Cos(el) * Math.Cos(az); position[i, 1] = Math.Cos(el) * Math.Sin(az); position[i, 2] = Math.Sin(el);
            }
            else for (var k = 0; k < 3; k++) position[i, k] = positions[i][k];
        }
        var receivers = new double[2, 3] { { 0, swapEars ? -0.09 : 0.09, 0 }, { 0, swapEars ? 0.09 : -0.09, 0 } };
        var file = new H5File
        {
            ["Data.IR"] = ir,
            ["Data.SamplingRate"] = new double[] { rate },
            ["SourcePosition"] = new H5Dataset(position) { Attributes = new() { ["Type"] = positionType } },
            ["ReceiverPosition"] = receivers,
        };
        file.Attributes["Title"] = "Test head";
        using var stream = new MemoryStream();
        file.Write(stream);
        return stream.ToArray();
    }

    [Fact]
    public void ASofaFile_IsReadAsEachEarsBandGainsByDirection()
    {
        var hrtf = MeasuredHrtf.Read(new MemoryStream(Sofa()), "head.sofa");
        Assert.Equal("Test head (head.sofa)", hrtf.Name);
        Assert.Equal(49, hrtf.Measurements);
        Assert.Equal(48_000, hrtf.SampleRateHz);
        var fromLeft = new Vec3(0, 1, 0);
        // 1.8² against 0.2²: 19 dB louder at the near ear
        Assert.Equal(19.08, 10 * Math.Log10(hrtf.PowerGain(0, 4, fromLeft) / hrtf.PowerGain(1, 4, fromLeft)), 1);
        Assert.Equal(hrtf.PowerGain(0, 4, fromLeft), hrtf.PowerGain(1, 4, new Vec3(0, -1, 0)), 9);
        // from behind the highs are cut, the lows not
        var ahead = new Vec3(1, 0, 0); var behind = new Vec3(-1, 0, 0);
        Assert.InRange(10 * Math.Log10(hrtf.PowerGain(0, 7, ahead) / hrtf.PowerGain(0, 7, behind)), 4, 30);
        Assert.InRange(10 * Math.Log10(hrtf.PowerGain(0, 1, ahead) / hrtf.PowerGain(0, 1, behind)), -0.1, 0.1);
        // a direction between measurements takes the nearest: 10 degrees off straight ahead is straight ahead
        Assert.Equal(hrtf.PowerGain(0, 4, ahead), hrtf.PowerGain(0, 4, new Vec3(Math.Cos(0.17), Math.Sin(0.17), 0)), 12);
    }

    [Fact]
    public void TheGains_AreEqualisedToTheDiffuseField()
    {
        var hrtf = MeasuredHrtf.Read(new MemoryStream(Sofa()));
        // the mean over the measured directions and both ears is 1 in every band
        var directions = new List<Vec3>();
        for (var el = -30; el <= 90; el += 30)
            for (var az = 0; az < 360; az += el == 90 ? 360 : 30)
                directions.Add(new Vec3(Math.Cos(el * Math.PI / 180) * Math.Cos(az * Math.PI / 180), Math.Cos(el * Math.PI / 180) * Math.Sin(az * Math.PI / 180), Math.Sin(el * Math.PI / 180)));
        for (var band = 0; band < 8; band++)
            Assert.Equal(1, directions.SelectMany(d => new[] { hrtf.PowerGain(0, band, d), hrtf.PowerGain(1, band, d) }).Average(), 9);
    }

    [Fact]
    public void TheLeftEar_IsFoundFromTheReceiverPositions_AndCartesianPositionsAreRead()
    {
        var swapped = MeasuredHrtf.Read(new MemoryStream(Sofa(swapEars: true, positionType: "cartesian")));
        var fromLeft = new Vec3(0, 1, 0);
        Assert.True(swapped.PowerGain(0, 4, fromLeft) > swapped.PowerGain(1, 4, fromLeft) * 10);
    }

    [Fact]
    public void AFileThatIsNotSofa_IsRefused()
    {
        Assert.Throws<FormatException>(() => MeasuredHrtf.Read(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })));
        var file = new H5File { ["Other"] = new double[] { 1 } };
        using var stream = new MemoryStream();
        file.Write(stream);
        Assert.Contains("not a SOFA", Assert.Throws<FormatException>(() => MeasuredHrtf.Read(new MemoryStream(stream.ToArray()))).Message);
    }

    [Fact]
    public void ARoomHeardThroughTheMeasuredHead_IsLouderInTheEarTowardsTheSource()
    {
        var room = Room3D.Shoebox(10, 8, 3, new SurfaceFinish(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)0.3, 8).ToArray())));
        var hrtf = MeasuredHrtf.Read(new MemoryStream(Sofa()));
        var source = new ExtendedSource(new[] { new SourcePoint(new Vec3(5, 6.5, 1.5)) });
        // facing +x, the source is on the left
        var heard = RoomAuralisation.Render(room, source, new Vec3(5, 3, 1.5), 0, new[] { 1.0 }, 16_000, new RayTraceOptions(RayCount: 30_000, ImageSourceOrder: 2), hrtf: hrtf);
        Assert.True(heard.InterauralLevelDifferenceDb > 3, $"{heard.InterauralLevelDifferenceDb} dB");
        var turned = RoomAuralisation.Render(room, source, new Vec3(5, 3, 1.5), 180, new[] { 1.0 }, 16_000, new RayTraceOptions(RayCount: 30_000, ImageSourceOrder: 2), hrtf: hrtf);
        Assert.True(turned.InterauralLevelDifferenceDb < -3, $"{turned.InterauralLevelDifferenceDb} dB");
    }
}
