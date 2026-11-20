using System.Collections.Concurrent;
using RoomAcoustics.Core;

namespace RoomAcoustics.Api;

/// <summary>Keeps the few most recent measured HRTFs (SOFA files) in memory, so a listening request can refer to one by id.</summary>
public static class HrtfService
{
    private const int MaxHrtfs = 4;
    private const int MaxBytes = 64 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, MeasuredHrtf> Store = new();
    private static readonly ConcurrentQueue<string> Order = new();

    public static MeasuredHrtf Get(string id) =>
        Store.TryGetValue(id, out var hrtf) ? hrtf : throw new ArgumentException("That measured HRTF is no longer loaded; upload it again.");

    public static HrtfDto Import(Stream data, string name)
    {
        using var buffer = new MemoryStream();
        data.CopyTo(buffer);
        if (buffer.Length == 0) throw new ArgumentException("The file is empty.");
        if (buffer.Length > MaxBytes) throw new ArgumentException("The file is larger than 64 MB.");
        buffer.Position = 0;
        MeasuredHrtf hrtf;
        try { hrtf = MeasuredHrtf.Read(buffer, name); }
        catch (FormatException ex) { throw new ArgumentException($"Could not read '{name}' as an HRTF: {ex.Message}"); }

        var id = Guid.NewGuid().ToString("N");
        Store[id] = hrtf;
        Order.Enqueue(id);
        while (Order.Count > MaxHrtfs && Order.TryDequeue(out var old)) Store.TryRemove(old, out _);
        return new HrtfDto(id, hrtf.Name, hrtf.Measurements, hrtf.SampleRateHz);
    }
}
