using RoomAcoustics.Core;

namespace RoomAcoustics.Api;

/// <summary>The ISO 3382-1 parameters of a measured impulse response, uploaded as a WAV file, to set beside a simulated receiver's.</summary>
public static class MeasuredService
{
    private const int MaxBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Reads the WAV and analyses it (<see cref="MeasuredImpulseResponse"/>). A file of several channels is analysed by its first: the left of a
    /// stereo or binaural pair, the omnidirectional W of an Ambisonic one.
    /// </summary>
    public static MeasuredDto Analyse(Stream data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new ArgumentException("The file is empty.");
        if (data.Length > MaxBytes) throw new ArgumentException("The file is larger than 256 MB.");
        WavAudio wav;
        try { wav = WavReader.Read(data); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            throw new ArgumentException($"Could not read '{name}' as a WAV file: {ex.Message}");
        }
        var samples = wav.Channels.Count > 0 ? wav.Channels[0] : wav.Samples;
        var analysis = MeasuredImpulseResponse.Analyse(samples, wav.SampleRateHz);
        var m = analysis.Metrics;
        double?[] Bands(OctaveSpectrum s) => s.Bands.Select(v => double.IsFinite(v) ? Math.Round(v, 4) : (double?)null).ToArray();
        return new MeasuredDto(name, wav.SampleRateHz, wav.OriginalChannelCount, Math.Round(analysis.DurationS, 4), Math.Round(analysis.DirectArrivalS, 4),
            analysis.DynamicRangeDb.Select(d => double.IsFinite(d) ? Math.Round(d, 1) : (double?)null).ToArray(),
            Bands(m.T30S), Bands(m.T20S), Bands(m.EarlyDecayTimeS), Bands(m.C50Db), Bands(m.C80Db), Bands(m.D50), Bands(m.CentreTimeS));
    }
}
