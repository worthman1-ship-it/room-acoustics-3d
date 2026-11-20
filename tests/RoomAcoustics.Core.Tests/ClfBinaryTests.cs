using System.Text;
using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The CLF Group's binary distribution file for third-octave loudspeakers (.CF2), read as a source pattern.</summary>
public class ClfBinaryTests
{
    /// <summary>
    /// A CF2 as the CLF authoring tool lays one out: the signature, the version and the text fields, then at byte 78 172 the balloon as 4-byte floats, band by band
    /// (23 third octaves, 100 Hz to 16 kHz), arc by arc (72, every 5 degrees of rotation from the top), angle by angle along the arc (37, every 5 degrees from the
    /// front), each the level in dB relative to on axis; the point straight behind is stored as 0. <paramref name="level"/> gives it from the band index, the
    /// rotation and the angle along the arc, in degrees.
    /// </summary>
    internal static byte[] Cf2(Func<int, double, double, double> level, string model = "TM-1", int length = 334_040)
    {
        var bytes = new byte[length];
        new byte[] { 0x41, 0xBD, 0x0A, 0x00, 0x01 }.CopyTo(bytes, 0);
        void Put(int at, string text) => Encoding.Latin1.GetBytes(text).CopyTo(bytes, at);
        Put(20, "v2.1a"); Put(52, "Test maker, Somewhere"); Put(312, model); Put(568, "Test maker"); Put(824, "A test speaker");
        for (var band = 0; band < 23; band++)
            for (var arc = 0; arc < 72; arc++)
                for (var angle = 0; angle < 37; angle++)
                {
                    var at = 78_172 + ((band * 72 + arc) * 37 + angle) * 4;
                    if (at + 4 <= length) BitConverter.GetBytes(angle == 36 ? 0f : (float)level(band, arc * 5, angle * 5)).CopyTo(bytes, at);
                }
        return bytes;
    }

    /// <summary>Quieter off axis, and more so towards the top (rotation 0) than the bottom (180): 20 dB at the back, times 1.5 at the top and 0.5 at the bottom.</summary>
    private static double TopHeavy(int band, double rotation, double angle) => -angle / 180 * 20 * (1 + 0.5 * Math.Cos(rotation * Math.PI / 180));

    [Fact]
    public void ACf2IsRecognisedByItsSignature_AndNamedByItsMakerAndModel()
    {
        var file = Cf2(TopHeavy);
        Assert.True(ClfReader.IsCf2(file));
        Assert.False(ClfReader.IsCf2(Encoding.ASCII.GetBytes("<CLF2>\n<MODELNAME>\tx")));
        Assert.False(ClfReader.IsCf2(new byte[] { 0x43, 0x46, 0x32, 0x00 }));
        var read = ClfReader.ParseCf2(file);
        Assert.Equal("TM-1", read.Model);
        Assert.Equal("Test maker", read.Manufacturer);
        Assert.Equal("A test speaker", read.Description);
    }

    [Fact]
    public void ItsArcsRunFromTheTop_AsInTheClfSpec_SoTheTopIsQuieterThanTheBottom()
    {
        var balloon = ClfReader.ParseCf2(Cf2(TopHeavy)).Balloon;
        double Re(double az, double el) => balloon.LevelDb(4, az, el) - balloon.LevelDb(4, 0, 0); // 1 kHz, re on axis
        Assert.Equal(-30.0 / 180 * 20 * 1.5, Re(0, 30), 1);  // 30 degrees up: along the top arc
        Assert.Equal(-30.0 / 180 * 20 * 0.5, Re(0, -30), 1); // 30 degrees down: along the bottom arc
        Assert.Equal(-10, Re(90, 0), 1);                     // to the side, half way round: the arcs at 90 and 270 have cos 0
        Assert.Equal(Re(90, 0), Re(270, 0), 6);              // left and right alike
    }

    [Fact]
    public void ThePointStraightBehind_StoredAsZero_IsTheEnergyMeanOfTheRingAroundIt()
    {
        var balloon = ClfReader.ParseCf2(Cf2(TopHeavy)).Balloon;
        var ring = Enumerable.Range(0, 72).Average(arc => Math.Pow(10, TopHeavy(10, arc * 5, 175) / 10));
        Assert.Equal(10 * Math.Log10(ring), balloon.LevelDb(4, 180, 0) - balloon.LevelDb(4, 0, 0), 1);
        Assert.True(balloon.LevelDb(4, 180, 0) - balloon.LevelDb(4, 0, 0) < -15, "not the 0 dB the file stores there");
    }

    [Fact]
    public void AFileWithTheSignatureButNotTheLayout_IsRefusedWithWhy()
    {
        var shortFile = Cf2(TopHeavy, length: 100_000);
        Assert.Contains("too short", Assert.Throws<FormatException>(() => ClfReader.ParseCf2(shortFile)).Message);
        // levels nowhere near 0 dB on axis: not a balloon where this layout puts one
        var shifted = Cf2((band, rotation, angle) => 40 + TopHeavy(band, rotation, angle));
        Assert.Contains("on axis", Assert.Throws<FormatException>(() => ClfReader.ParseCf2(shifted)).Message);
        var empty = Cf2((band, rotation, angle) => float.NaN);
        Assert.Contains("no balloon", Assert.Throws<FormatException>(() => ClfReader.ParseCf2(empty)).Message);
    }

    [Fact]
    public void AFileMeasuredTo10kHz_WithJunkInTheBandsAbove_IsReadToItsLastBand()
    {
        // as JBL's Control 62P: 12.5 and 16 kHz hold whatever was in memory (here 1e30 and NaN)
        var file = Cf2((band, rotation, angle) => band == 21 ? 1e30 : band == 22 ? double.NaN : TopHeavy(band, rotation, angle));
        var balloon = ClfReader.ParseCf2(file).Balloon;
        Assert.Equal(-30.0 / 180 * 20 * 1.5, balloon.LevelDb(7, 0, 30) - balloon.LevelDb(7, 0, 0), 1); // 8 kHz, from 6.3 to 10 kHz
    }

    [Fact]
    public void ABandOnAxisAt0dBOnlyOnSomeArcs_OrLouderOffAxis_IsRead()
    {
        // as JBL's CBT 70J narrow at 16 kHz (on axis -10 dB on some arcs, 0.5 on others) and its Control 62P at 250 Hz (6.5 dB straight behind)
        var file = Cf2((band, rotation, angle) =>
            band == 22 && angle == 0 ? (rotation < 180 ? -10 : 0.5) : band == 4 && angle == 35 ? 6.5 : TopHeavy(band, rotation, angle));
        Assert.Equal("TM-1", ClfReader.ParseCf2(file).Model);
    }
}
