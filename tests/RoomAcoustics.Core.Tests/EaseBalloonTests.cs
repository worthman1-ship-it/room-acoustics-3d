using System.Text;
using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>An EASE 3 or 4 speaker's directivity balloon (.lob), read as a source pattern.</summary>
public class EaseBalloonTests
{
    /// <summary>
    /// A .lob as EASE 3 or 4 lays one out (as in Genelec's EASE downloads): "EASE", 0 (EASE 3) or 1 (EASE 4) at byte 12, the band count at byte 18, a name at
    /// byte 30 (16 characters in EASE 3, length-prefixed in EASE 4) and 5 348 bytes, then a record for each of 21 third-octave bands (100 Hz to 10 kHz): a name
    /// in the same form, 20 bytes, then 72 rows of 37 two-byte integers, the attenuation in hundredths of a dB (0 on axis), row k at 5k degrees of rotation from
    /// the left towards the top, angle j at 5j degrees from the front. <paramref name="attenuation"/> gives it in dB from the band index, the rotation from the
    /// left and the angle from the front.
    /// </summary>
    internal static byte[] Lob(Func<int, double, double, double> attenuation, int bands = 21, bool phase = false, bool ease4 = false)
    {
        var data = new List<byte>();
        void Name(string name)
        {
            if (ease4) { data.AddRange(BitConverter.GetBytes((short)name.Length)); data.AddRange(Encoding.ASCII.GetBytes(name)); }
            else data.AddRange(Encoding.ASCII.GetBytes(name.PadRight(16)));
        }
        data.AddRange(Encoding.ASCII.GetBytes("EASE"));
        data.AddRange(new byte[] { 0x03, 0x00, 0x01, 0x00, 0x14, 0x00, 0x68, 0x6E, (byte)(ease4 ? 1 : 0), 0, 0, 0, 0, 0 });
        data.AddRange(BitConverter.GetBytes(bands));
        data.AddRange(BitConverter.GetBytes(ease4 ? 5352 : 5364));
        data.AddRange(BitConverter.GetBytes(31));
        Name("Speaker");
        data.AddRange(new byte[5348]);
        for (var band = 0; band < bands; band++)
        {
            Name("Speaker");
            data.AddRange(new byte[] { 0x02, 0x00 });
            data.AddRange(new byte[12]);
            data.AddRange(new byte[] { 0x88, 0x78, 0x00, 0x00, 0x00, 0x00 });
            for (var row = 0; row < 72; row++)
                for (var angle = 0; angle < 37; angle++)
                {
                    var value = phase ? -6500 - 100 * band - angle : (short)Math.Round(100 * attenuation(band, row * 5, angle * 5));
                    data.AddRange(BitConverter.GetBytes((short)value));
                }
        }
        return data.ToArray();
    }

    /// <summary>Quieter off axis, more so above (rotation 90 from the left) than below, and more to the right (180) than the left (0).</summary>
    private static double Lopsided(int band, double fromLeft, double angle)
    {
        var r = fromLeft * Math.PI / 180;
        return angle / 180 * 20 * (1 + 0.5 * Math.Sin(r) + 0.25 * Math.Cos(r + Math.PI));
    }

    [Fact]
    public void AnEaseBalloonIsRecognised_AndNamedByItsFile()
    {
        var lob = Lob(Lopsided);
        Assert.True(EaseReader.IsEase(lob));
        Assert.False(EaseReader.IsEase(Encoding.ASCII.GetBytes("<CLF2>")));
        Assert.Equal("8030A", EaseReader.ParseBalloon(lob, "8030A").Model);
    }

    [Fact]
    public void ItsRowsTurnFromTheLeftThroughTheTop_SoAboveIsQuieterThanBelow_AndRightThanLeft()
    {
        var balloon = EaseReader.ParseBalloon(Lob(Lopsided), "test").Balloon;
        double Re(double az, double el) => balloon.LevelDb(4, az, el) - balloon.LevelDb(4, 0, 0); // 1 kHz, re on axis
        Assert.Equal(-Lopsided(10, 90, 30), Re(0, 30), 1);   // 30 degrees up: the row at 90 from the left
        Assert.Equal(-Lopsided(10, 270, 30), Re(0, -30), 1); // 30 degrees down
        Assert.Equal(-Lopsided(10, 0, 60), Re(60, 0), 1);    // 60 degrees to the left (the source's +y): the row at 0
        Assert.Equal(-Lopsided(10, 180, 60), Re(-60, 0), 1); // 60 degrees to the right
        Assert.True(Re(0, 30) < Re(0, -30) && Re(-60, 0) < Re(60, 0));
    }

    [Fact]
    public void AnEase4Balloon_WithItsNamesGivenByLength_ReadsAsTheSameEase3One()
    {
        var three = EaseReader.ParseBalloon(Lob(Lopsided), "x").Balloon;
        var four = EaseReader.ParseBalloon(Lob(Lopsided, ease4: true), "x").Balloon;
        foreach (var (az, el) in new[] { (0.0, 0.0), (0.0, 30.0), (60.0, 0.0), (-60.0, 0.0), (180.0, 0.0) })
            Assert.Equal(three.LevelDb(4, az, el), four.LevelDb(4, az, el), 9);
    }

    [Fact]
    public void ThePhaseBalloon_ADifferentBandCount_OrAFileWithNoBalloon_IsRefusedWithWhy()
    {
        Assert.Contains("phase", Assert.Throws<FormatException>(() => EaseReader.ParseBalloon(Lob(Lopsided, phase: true), "x")).Message);
        Assert.Contains("21", Assert.Throws<FormatException>(() => EaseReader.ParseBalloon(Lob(Lopsided, bands: 31), "x")).Message);
        var spk = Encoding.ASCII.GetBytes("EASE").Concat(new byte[4000]).ToArray();
        Assert.Contains(".lob", Assert.Throws<FormatException>(() => EaseReader.ParseBalloon(spk, "x")).Message);
        var cut = Lob(Lopsided)[..^100];
        Assert.Contains("laid out differently", Assert.Throws<FormatException>(() => EaseReader.ParseBalloon(cut, "x")).Message);
    }
}
