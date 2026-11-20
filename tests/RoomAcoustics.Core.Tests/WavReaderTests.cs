using System.Text;
using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class WavReaderTests
{
    /// <summary>A WAV file of the given format whose data chunk holds <paramref name="data"/>, with a LIST chunk before it to be skipped.</summary>
    private static MemoryStream Wav(short format, short channels, int rate, short bits, byte[] data)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        var list = Encoding.ASCII.GetBytes("INFOISFT\u0005\0\0\0test\0\0"); // an odd-sized sub-chunk, padded
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(4 + 24 + 8 + list.Length + 8 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write(format); w.Write(channels); w.Write(rate);
        w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write(bits);
        w.Write(Encoding.ASCII.GetBytes("LIST")); w.Write(list.Length); w.Write(list);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
        w.Flush();
        s.Position = 0;
        return s;
    }

    [Fact]
    public void SixteenBitStereo_IsReadAsItsChannels_AndTheirMean()
    {
        // two frames: left 0.5 then -0.25, right 0 then 1 (32767 / 32768)
        var data = new List<byte>();
        foreach (short v in new short[] { 16384, 0, -8192, 32767 }) data.AddRange(BitConverter.GetBytes(v));
        var wav = WavReader.Read(Wav(1, 2, 48000, 16, data.ToArray()));

        Assert.Equal(48000, wav.SampleRateHz);
        Assert.Equal(2, wav.OriginalChannelCount);
        Assert.Equal(new[] { 0.5, -0.25 }, wav.Channels[0]);
        Assert.Equal(32767 / 32768.0, wav.Channels[1][1], 12);
        Assert.Equal(0.25, wav.Samples[0], 12);
    }

    [Fact]
    public void TwentyFourBitPcm_AndFloat_AreRead()
    {
        // 24-bit: -0.5 and just under 1
        var wav = WavReader.Read(Wav(1, 1, 44100, 24, new byte[] { 0x00, 0x00, 0xC0, 0xFF, 0xFF, 0x7F }));
        Assert.Equal(-0.5, wav.Samples[0], 12);
        Assert.Equal(8388607 / 8388608.0, wav.Samples[1], 12);

        var floats = new List<byte>();
        foreach (var v in new[] { 0.75f, -1f }) floats.AddRange(BitConverter.GetBytes(v));
        var f = WavReader.Read(Wav(3, 1, 96000, 32, floats.ToArray()));
        Assert.Equal(new[] { 0.75, -1.0 }, f.Samples);
        Assert.Equal(32, f.BitsPerSample);
    }

    [Fact]
    public void ACompressedOrForeignFile_IsRefused()
    {
        Assert.Throws<NotSupportedException>(() => WavReader.Read(Wav(2, 1, 48000, 4, new byte[] { 1, 2 })));
        Assert.Throws<InvalidDataException>(() => WavReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("OggS and some more bytes"))));
    }
}
