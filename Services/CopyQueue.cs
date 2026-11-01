using System.Collections.ObjectModel;
using EveryCopy.Models;

namespace EveryCopy.Services;

/// <summary>
/// Runs transfers in the order they were added, like TeraCopy's queue: two jobs fighting over the same disk are slower than
/// either alone. Jobs that fill different drives (several USB sticks at once) do run side by side, each with its own colour.
/// </summary>
public sealed class CopyQueue
{
    /// <summary>How many different drives may be written to at the same moment.</summary>
    public const int MaxParallel = 4;

    private readonly object gate = new();
    private readonly List<CopyJob> order = new(); // run order; the UI list below mirrors it
    private readonly HashSet<CopyJob> started = new();
    private readonly HashSet<string> busyRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bound to the UI; only touch it from the UI thread.</summary>
    public ObservableCollection<CopyJob> Jobs { get; } = new();

    public CopyJob Enqueue(IReadOnlyList<string> sources, string destination, JobOptions options)
    {
        var job = new CopyJob(sources, destination, options);

        // Colour first, so the card is tagged from its first frame. Every drive still listed keeps its own colour, finished
        // transfers included, so a card that just turned "Done" is never mistaken for the one that starts next to it.
        var inUse = Jobs.Select(j => j.DestKey);
        Destinations.Assign(job.DestKey, inUse);

        lock (gate) order.Add(job);
        Jobs.Add(job);
        Pump();
        return job;
    }

    /// <summary>Starts every waiting job whose drive is free, oldest first, up to <see cref="MaxParallel"/> at once.</summary>
    private void Pump()
    {
        while (true)
        {
            CopyJob? next;
            lock (gate)
            {
                next = started.Count >= MaxParallel ? null
                    : order.FirstOrDefault(j => j.State == JobState.Queued && !started.Contains(j) && !busyRoots.Contains(j.DestRoot));
                if (next == null) return;
                started.Add(next);
                busyRoots.Add(next.DestRoot);
            }

            var job = next;
            _ = Task.Run(async () =>
            {
                try { await CopyEngine.RunAsync(job); }
                catch (Exception ex)
                {
                    // RunAsync reports its own failures; this keeps the queue alive if something unforeseen escapes.
                    Log.Write("Queue worker error", ex);
                    job.FatalError = ex.Message;
                    job.State = JobState.Failed;
                }
                finally
                {
                    lock (gate) { started.Remove(job); busyRoots.Remove(job.DestRoot); }
                    Pump();
                }
            });
        }
    }

    public bool HasActive => Jobs.Any(j => !j.IsFinished);

    public void CancelAll()
    {
        foreach (var job in Jobs) job.Cancel();
    }

    public void RemoveFinished()
    {
        for (int i = Jobs.Count - 1; i >= 0; i--)
            if (Jobs[i].IsFinished) Remove(Jobs[i]);
    }

    public void Remove(CopyJob job)
    {
        lock (gate) order.Remove(job);
        Jobs.Remove(job);
    }

    /// <summary>Moves a waiting job one place earlier (<paramref name="delta"/> -1) or later (+1), swapping with a neighbour that is also waiting.</summary>
    public bool Move(CopyJob job, int delta)
    {
        int i = Jobs.IndexOf(job), j = i + delta;
        if (i < 0 || j < 0 || j >= Jobs.Count || job.State != JobState.Queued || Jobs[j].State != JobState.Queued) return false;
        lock (gate)
        {
            int a = order.IndexOf(job), b = order.IndexOf(Jobs[j]);
            if (a < 0 || b < 0) return false;
            (order[a], order[b]) = (order[b], order[a]);
        }
        Jobs.Move(i, j);
        return true;
    }
}
