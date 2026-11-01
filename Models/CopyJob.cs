using System.Collections.Concurrent;
using System.ComponentModel;

namespace EveryCopy.Models;

public enum TransferMode { Copy, Move }

/// <summary>What to do when a file already exists at the destination.</summary>
public enum CollisionPolicy { KeepBoth, Overwrite, Skip, OverwriteOlder }

public enum JobState { Queued, Scanning, Running, Paused, Done, DoneWithErrors, Failed, Cancelled }

/// <param name="Threads">Small files copied at once; 0 lets EveryCopy choose from the drives involved.</param>
/// <param name="Resume">Carry on an earlier run: files already copied intact are skipped and half-copied ones are finished.</param>
public sealed record JobOptions(TransferMode Mode, CollisionPolicy Collision, bool Verify, int Threads, bool Resume = false);

public sealed record FileFailure(string Path, string Message);

/// <summary>
/// One queued transfer. The engine threads update the counters; the UI calls <see cref="TryRefresh"/> on a timer,
/// which raises a single "everything changed" notification, so no property is ever raised from a worker thread.
/// </summary>
public sealed class CopyJob : INotifyPropertyChanged
{
    private long totalBytes, doneWork;
    private int totalFiles, doneFiles, skippedFiles, failedFiles;
    private volatile string currentFile = "";
    private volatile string? fatal;
    private int state;
    private int waiting;
    private volatile string waitText = "";
    private bool shownFinal;

    private readonly ConcurrentDictionary<string, byte> excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.OrdinalIgnoreCase);

    public CopyJob(IReadOnlyList<string> sources, string destination, JobOptions options)
    {
        Sources = sources;
        Destination = destination;
        Options = options;
        Items = sources.Select(s => new JobItem(this, Normalize(s))).ToList();
        DestRoot = EveryCopy.Services.Destinations.RootOf(destination);
        DestKey = EveryCopy.Services.Destinations.KeyOf(destination);
    }

    // ---- which drive this fills: its colour and name, so several people can follow their own USB stick -----------

    /// <summary>The drive or share being filled, such as <c>E:\</c>; two transfers to the same one never run at the same time.</summary>
    public string DestRoot { get; }

    /// <summary>The drive's identity in the colour/name book (its volume serial number).</summary>
    public string DestKey { get; }

    /// <summary>Index into <see cref="EveryCopy.Services.Destinations.Palette"/>; follows the drive, so renaming or recolouring it changes every transfer to it.</summary>
    public int DestColor => EveryCopy.Services.Destinations.ColorOf(DestKey);
    public string DestColorName => EveryCopy.Services.Destinations.Palette[DestColor].Name;

    /// <summary>The user's name for the drive, or empty.</summary>
    public string DestName => EveryCopy.Services.Destinations.NameOf(DestKey);

    /// <summary>What the transfer is tagged with: "Sara's USB · Pink", or just "Pink".</summary>
    public string DestLabel => EveryCopy.Services.Destinations.Label(DestKey);

    /// <summary>Whether the detailed layout of the progress window is showing for this transfer.</summary>
    public bool ShowDetails { get; set; }

    /// <summary>Asks every binding to re-read, for a change made outside the normal ticks (a rename, a colour).</summary>
    public void RaiseChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    // ---- removing items while the job runs ----------------------------------------------------------------------

    /// <summary>The top-level files and folders of this transfer; each can be removed, even while it is being copied.</summary>
    public IReadOnlyList<JobItem> Items { get; }

    public string ItemsHeader
    {
        get
        {
            int left = Items.Count(i => !i.Removed);
            return left == Items.Count ? $"Items ({Items.Count})" : $"Items ({left} of {Items.Count})";
        }
    }

    /// <summary>True if <paramref name="path"/> is a removed item or inside one.</summary>
    public bool IsExcluded(string path)
    {
        if (excluded.IsEmpty) return false;
        foreach (string root in excluded.Keys)
            if (Under(path, root)) return true;
        return false;
    }

    private static bool Under(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Takes an item out of the transfer. If it is being copied right now, that copy stops and its partial file is deleted.</summary>
    public void Remove(JobItem item)
    {
        if (IsFinished || item.Removed) return;
        item.Removed = true;
        excluded[item.Path] = 0;
        foreach (var (file, cts) in active)
            if (Under(file, item.Path)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    /// <summary>Stops the file(s) being copied right now and moves on to the next; the items stay in the list.</summary>
    public void SkipCurrent()
    {
        foreach (var cts in active.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        Gate.Set();
    }

    public void Register(string file, CancellationTokenSource cts) => active[file] = cts;
    public void Unregister(string file) => active.TryRemove(file, out _);

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<string> Sources { get; }
    public string Destination { get; }
    public JobOptions Options { get; }
    public ConcurrentQueue<FileFailure> Failures { get; } = new();
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Open while running, closed while paused; workers wait on it between chunks.</summary>
    public ManualResetEventSlim Gate { get; } = new(true);

    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public double Speed { get; set; }

    // ---- speed graph: one sample every half second, the last minute kept ------------------------------------

    private const int HistoryLength = 120;
    private readonly List<double> history = new();
    private double peak;

    public void PushSpeed(double bytesPerSecond)
    {
        lock (history)
        {
            history.Add(bytesPerSecond);
            if (history.Count > HistoryLength) history.RemoveAt(0);
            if (bytesPerSecond > peak) peak = bytesPerSecond;
        }
    }

    /// <summary>A copy of the recent speed samples (bytes per second), oldest first.</summary>
    public double[] SpeedHistory { get { lock (history) return history.ToArray(); } }

    public double PeakSpeed { get { lock (history) return peak; } }
    public string PercentText => $"{Percent:0}%";

    public JobState State
    {
        get => (JobState)Volatile.Read(ref state);
        set => Volatile.Write(ref state, (int)value);
    }

    public string CurrentFile { get => currentFile; set => currentFile = value; }
    public string? FatalError { get => fatal; set => fatal = value; }

    public long TotalBytes => Volatile.Read(ref totalBytes);
    public int TotalFiles => Volatile.Read(ref totalFiles);
    public int DoneFiles => Volatile.Read(ref doneFiles);
    public int SkippedFiles => Volatile.Read(ref skippedFiles);
    public int FailedFiles => Volatile.Read(ref failedFiles);

    /// <summary>Bytes of work in total: every file is read once to copy it and once more to verify it.</summary>
    public long TotalWork => TotalBytes * (Options.Verify ? 2 : 1);
    public long DoneWork => Volatile.Read(ref doneWork);

    public void SetTotals(int files, long bytes)
    {
        Volatile.Write(ref totalFiles, files);
        Volatile.Write(ref totalBytes, bytes);
    }

    public void AddWork(long bytes) => Interlocked.Add(ref doneWork, bytes);
    public void AddDone() => Interlocked.Increment(ref doneFiles);
    public void AddSkipped() => Interlocked.Increment(ref skippedFiles);

    public void Fail(string path, string message)
    {
        Failures.Enqueue(new FileFailure(path, message));
        Interlocked.Increment(ref failedFiles);
    }

    /// <summary>A source or destination went away; the engine is waiting for it to come back.</summary>
    public string? DeviceWait => Volatile.Read(ref waiting) > 0 ? waitText : null;

    public void EnterWait(string what)
    {
        waitText = what;
        Interlocked.Increment(ref waiting);
    }

    public void LeaveWait() => Interlocked.Decrement(ref waiting);

    public void Pause()
    {
        if (State != JobState.Running) return;
        Gate.Reset();
        State = JobState.Paused;
    }

    public void Resume()
    {
        if (State != JobState.Paused) return;
        State = JobState.Running;
        Gate.Set();
    }

    public void Cancel()
    {
        if (IsFinished) return;
        if (State == JobState.Queued) State = JobState.Cancelled;
        Cts.Cancel();
        Gate.Set(); // let a paused worker see the cancellation
    }

    // ---- what the UI shows ------------------------------------------------------------------------------------

    public bool IsFinished => State is JobState.Done or JobState.DoneWithErrors or JobState.Failed or JobState.Cancelled;
    public bool IsActive => State is JobState.Scanning or JobState.Running or JobState.Paused;
    public bool IsScanning => State == JobState.Scanning;
    public bool IsPaused => State == JobState.Paused;
    public bool CanPause => State == JobState.Running;
    public bool CanResume => State == JobState.Paused;
    public bool CanCancel => !IsFinished;
    public bool CanRemove => IsFinished || State == JobState.Queued;
    public bool CanMove => State == JobState.Queued;
    public bool CanRetry => IsFinished && (HasErrors || State is JobState.Failed or JobState.Cancelled);
    public bool CanSkipFile => State is JobState.Running or JobState.Paused;

    /// <summary>The speed shown beside the percentage.</summary>
    public string SpeedText => State switch
    {
        JobState.Running when DeviceWait != null => "waiting",
        JobState.Running => $"{Format.Bytes((long)Speed)}/s",
        JobState.Paused => "paused",
        JobState.Done or JobState.DoneWithErrors when (FinishedUtc - StartedUtc).TotalSeconds > 0.5 =>
            $"{Format.Bytes((long)(TotalBytes / (FinishedUtc - StartedUtc).TotalSeconds))}/s avg",
        _ => "",
    };
    public bool HasErrors => FailedFiles > 0;
    public string ErrorsLabel => FailedFiles == 1 ? "1 error" : $"{FailedFiles} errors";

    public double Percent
    {
        get
        {
            if (State is JobState.Done or JobState.DoneWithErrors) return 100;
            long total = TotalWork;
            if (total <= 0) return TotalFiles > 0 ? 100.0 * (DoneFiles + SkippedFiles + FailedFiles) / TotalFiles : 0;
            return Math.Min(100, 100.0 * DoneWork / total);
        }
    }

    public string Title
    {
        get
        {
            string verb = Options.Mode == TransferMode.Move ? "Move" : "Copy";
            return Sources.Count == 1 ? $"{verb} {FromName}" : $"{verb} {Sources.Count} items";
        }
    }

    private static string NameOf(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return name.Length == 0 ? path : name;
    }

    /// <summary>The source's own name; with several sources, the first one and a count.</summary>
    public string FromName => Sources.Count == 1 ? NameOf(Sources[0]) : $"{NameOf(Sources[0])} + {Sources.Count - 1} more";
    public string FromPath => Path.GetDirectoryName(Sources[0].TrimEnd('\\', '/')) ?? Sources[0];
    public string FromTip => string.Join("\n", Sources);
    public string ToName => NameOf(Destination);
    public string ToPath => Destination;

    public string StatusText => DeviceWait != null && IsActive ? "Waiting for drive" : State switch
    {
        JobState.Queued => "Waiting",
        JobState.Scanning => "Scanning",
        JobState.Running => Options.Mode == TransferMode.Move ? "Moving" : "Copying",
        JobState.Paused => "Paused",
        JobState.Done => "Done",
        JobState.DoneWithErrors => "Done with errors",
        JobState.Failed => "Failed",
        _ => "Cancelled",
    };

    public string Detail
    {
        get
        {
            if (State == JobState.Queued) return "Waiting for the transfers before it";
            if (DeviceWait != null && IsActive) return DeviceWait + ". It continues by itself when it is back.";
            if (State == JobState.Scanning) return $"{TotalFiles:N0} files found so far";
            if (State == JobState.Failed) return FatalError ?? "The transfer could not start";

            string files = $"{DoneFiles + SkippedFiles + FailedFiles:N0} of {TotalFiles:N0} files";

            if (IsFinished)
            {
                var took = FinishedUtc - StartedUtc;
                bool ok = State is JobState.Done or JobState.DoneWithErrors;
                string avg = ok && took.TotalSeconds > 0.5 ? $" · {Format.Bytes((long)(TotalBytes / took.TotalSeconds))}/s average" : "";
                string skipped = SkippedFiles > 0 ? $" · {SkippedFiles:N0} skipped" : "";
                return $"{files} · {Format.Bytes(TotalBytes)} in {Format.Duration(took)}{avg}{skipped}";
            }

            long copied = Options.Verify ? DoneWork / 2 : DoneWork;
            string size = $"{Format.Bytes(Math.Min(copied, TotalBytes))} of {Format.Bytes(TotalBytes)}";
            string speed = State == JobState.Paused ? "paused" : $"{Format.Bytes((long)Speed)}/s";
            string eta = State == JobState.Running && Speed > 1
                ? $" · {Format.Duration(TimeSpan.FromSeconds(Math.Max(0, TotalWork - DoneWork) / Speed))} left" : "";
            return $"{files} · {size} · {speed}{eta}";
        }
    }

    public string Current => IsActive ? CurrentFile : "";

    /// <summary>Raises "everything changed" once per tick while active, and one last time after finishing.</summary>
    public bool TryRefresh()
    {
        if (IsFinished && shownFinal) return false;
        shownFinal = IsFinished;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var item in Items) item.Refresh();
        return true;
    }
}

/// <summary>One top-level file or folder of a transfer, with its own remove button.</summary>
public sealed class JobItem : INotifyPropertyChanged
{
    private readonly CopyJob owner;
    private volatile bool removed;

    public JobItem(CopyJob owner, string path)
    {
        this.owner = owner;
        Path = path;
        IsFolder = Directory.Exists(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public bool IsFolder { get; }
    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";
    public string Name { get { string n = System.IO.Path.GetFileName(Path); return n.Length == 0 ? Path : n; } }
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? "";

    public bool Removed { get => removed; set => removed = value; }
    public bool CanRemove => !removed && !owner.IsFinished;
    public string StatusText => removed ? "Removed" : "";
    public string RemovedLabel => removed ? "Removed" : "";

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public static class Format
{
    public static string Bytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{n:N0} B" : $"{v:0.#} {units[i]}";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        if (t.TotalSeconds >= 1) return $"{t.Minutes}:{t.Seconds:00}";
        return "<1 s";
    }
}

