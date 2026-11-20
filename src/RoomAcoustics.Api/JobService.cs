using System.Collections.Concurrent;
using System.Diagnostics;

namespace RoomAcoustics.Api;

/// <summary>
/// A calculation running in the background: the browser starts it, asks how far it has got, can cancel it, and collects the result when it is done.
/// A long simulation then shows a progress bar and stops using the processor when the user gives up on it.
/// </summary>
public sealed class Job
{
    private double _progress;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal Job(string id, CancellationTokenSource cancel) { Id = id; Cancel = cancel; }

    public string Id { get; }
    internal CancellationTokenSource Cancel { get; }
    public string Status { get; internal set; } = "running"; // running, done, failed or cancelled
    public object? Result { get; internal set; }
    public string? Error { get; internal set; }
    internal DateTime FinishedUtc { get; set; }

    public double Progress { get => Volatile.Read(ref _progress); internal set => Volatile.Write(ref _progress, value); }
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
    internal void StopClock() => _clock.Stop();
}

public static class JobService
{
    private const int MaxJobs = 40;
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, Job> Jobs = new();

    /// <summary>Starts <paramref name="work"/> on a pool thread. It is given a cancellation token and a function to report progress (0 to 1).</summary>
    public static Job Start(Func<CancellationToken, Action<double>, object> work)
    {
        Prune();
        var job = new Job(Guid.NewGuid().ToString("N"), new CancellationTokenSource());
        Jobs[job.Id] = job;
        _ = Task.Run(() =>
        {
            try
            {
                // progress comes from several threads at once and can arrive slightly out of order: keep the highest
                void Report(double p) { if (p > job.Progress) job.Progress = Math.Min(1, p); }
                job.Result = work(job.Cancel.Token, Report);
                job.Progress = 1;
                job.Status = "done";
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                job.Status = "failed";
                job.Error = ex.Message;
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.Error = "The calculation failed unexpectedly: " + ex.Message;
            }
            finally
            {
                job.StopClock();
                job.FinishedUtc = DateTime.UtcNow;
            }
        });
        return job;
    }

    /// <summary>The job, or null if there is no such job (finished jobs are forgotten after ten minutes).</summary>
    public static Job? Get(string id) => Jobs.TryGetValue(id, out var job) ? job : null;

    public static bool Cancel(string id)
    {
        if (!Jobs.TryGetValue(id, out var job)) return false;
        job.Cancel.Cancel();
        return true;
    }

    private static void Prune()
    {
        var old = Jobs.Values.Where(j => j.Status != "running" && DateTime.UtcNow - j.FinishedUtc > KeepFor).Select(j => j.Id).ToList();
        // and, if there are still too many, the oldest finished ones
        if (Jobs.Count - old.Count > MaxJobs)
            old.AddRange(Jobs.Values.Where(j => j.Status != "running").OrderBy(j => j.FinishedUtc).Take(Jobs.Count - old.Count - MaxJobs).Select(j => j.Id));
        foreach (var id in old.Distinct())
            if (Jobs.TryRemove(id, out var removed)) removed.Cancel.Dispose();
    }
}
