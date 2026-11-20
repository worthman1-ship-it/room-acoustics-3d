namespace RoomAcoustics.Core;

/// <summary>A decoded WAV file: mono samples (downmixed if the source was multi-channel) plus format info.</summary>
public sealed record WavAudio(double[] Samples, int SampleRateHz, int OriginalChannelCount, int BitsPerSample)
{
    /// <summary>The file's channels as they were recorded (first is left in a stereo file), each as long as <see cref="Samples"/>; <see cref="Samples"/> is their average.</summary>
    public IReadOnlyList<double[]> Channels { get; init; } = [];
}

/// <summary>
/// Minimal WAV (RIFF/PCM) file reader. Handles the standard uncompressed
/// formats produced by ordinary recording/editing tools (Windows Sound
/// Recorder, Audacity, most digital recorders): 8/16/24/32-bit integer PCM
/// and 32/64-bit IEEE float, any channel count (downmixed to mono by
/// averaging channels - simplest and sufficient for octave-band/spectrogram
/// analysis, where a single representative signal is what's needed).
/// Compressed WAV variants (e.g. ADPCM, MP3-in-WAV) are not supported - real
/// acoustic measurement recordings are essentially always uncompressed PCM,
/// so this deliberately does not pull in a general-purpose audio codec
/// library for formats unlikely to matter here.
/// </summary>
public static class WavReader
{
    private const short FormatPcm = 1;
    private const short FormatIeeeFloat = 3;

    public static WavAudio Read(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Read(stream);
    }

    public static WavAudio Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException("Not a RIFF file (missing 'RIFF' header).");
        reader.ReadInt32(); // RIFF chunk size, unused
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("Not a WAVE file (missing 'WAVE' identifier).");

        short formatTag = 0, channels = 0, bitsPerSample = 0;
        int sampleRate = 0;
        byte[]? data = null;

        while (stream.Position < stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                formatTag = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate, unused
                reader.ReadInt16(); // block align, unused
                bitsPerSample = reader.ReadInt16();
                var consumed = 16;
                if (chunkSize > consumed)
                    reader.ReadBytes(chunkSize - consumed); // skip any extension bytes
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }
            else
            {
                // Skip any other chunk (LIST, fact, cue, etc.) we don't need.
                reader.ReadBytes(chunkSize);
            }

            if (chunkSize % 2 == 1 && stream.Position < stream.Length)
                reader.ReadByte(); // chunks are word-aligned; skip the pad byte

            if (formatTag != 0 && data is not null)
                break;
        }

        if (formatTag == 0)
            throw new InvalidDataException("No 'fmt ' chunk found.");
        if (data is null)
            throw new InvalidDataException("No 'data' chunk found.");
        if (formatTag != FormatPcm && formatTag != FormatIeeeFloat)
            throw new NotSupportedException(
                $"Unsupported WAV format tag {formatTag} - only uncompressed PCM (1) and IEEE float (3) are supported.");

        var (mono, perChannel) = Decode(data, formatTag, channels, bitsPerSample);
        return new WavAudio(mono, sampleRate, channels, bitsPerSample) { Channels = perChannel };
    }

    private static (double[] Mono, double[][] Channels) Decode(byte[] data, short formatTag, short channels, short bitsPerSample)
    {
        var bytesPerSample = bitsPerSample / 8;
        var bytesPerFrame = bytesPerSample * channels;
        var frameCount = data.Length / bytesPerFrame;
        var mono = new double[frameCount];
        var perChannel = Enumerable.Range(0, channels).Select(_ => new double[frameCount]).ToArray();

        for (var frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = frame * bytesPerFrame + ch * bytesPerSample;
                var value = ReadSample(data, offset, formatTag, bitsPerSample);
                perChannel[ch][frame] = value;
                sum += value;
            }
            mono[frame] = sum / channels;
        }

        return (mono, perChannel);
    }

    /// <summary>Reads one sample, normalised to the range [-1, 1].</summary>
    private static double ReadSample(byte[] data, int offset, short formatTag, short bitsPerSample)
    {
        if (formatTag == FormatIeeeFloat)
        {
            return bitsPerSample switch
            {
                32 => BitConverter.ToSingle(data, offset),
                64 => BitConverter.ToDouble(data, offset),
                _ => throw new NotSupportedException($"Unsupported IEEE float bit depth: {bitsPerSample}"),
            };
        }

        return bitsPerSample switch
        {
            8 => (data[offset] - 128) / 128.0, // 8-bit PCM is unsigned
            16 => BitConverter.ToInt16(data, offset) / 32768.0,
            24 => Read24BitSigned(data, offset) / 8388608.0,
            32 => BitConverter.ToInt32(data, offset) / 2147483648.0,
            _ => throw new NotSupportedException($"Unsupported PCM bit depth: {bitsPerSample}"),
        };
    }

    private static int Read24BitSigned(byte[] data, int offset)
    {
        var value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000); // sign-extend
        return value;
    }
}
