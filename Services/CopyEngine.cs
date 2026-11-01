using System.IO.Enumeration;
using EveryCopy.Models;

namespace EveryCopy.Services;

/// <summary>
/// Runs one <see cref="CopyJob"/>. Files are copied one after another by default; the "files at once" option lets small
/// files overlap, while big files always go one at a time so the disks stream instead of seeking between copies.
/// If a drive or share disappears mid-copy the engine waits for it and carries on, resuming big files where they stopped.
/// </summary>
public static class CopyEngine
{
    /// <summary>Files under this size may be copied in parallel; larger ones one after another.</summary>
    private const long SmallFile = 32L * 1024 * 1024;
    /// <summary>Files this big are copied in restartable mode, so an interruption resumes instead of starting over.</summary>
    private const long RestartableFrom = 64L * 1024 * 1024;
    private const int VerifyChunk = 4 * 1024 * 1024;
    private const int ErrorDiskFull = 112, ErrorHandleDiskFull = 39;

    /// <summary>Win32 errors that mean "the other end went away" rather than "this file is bad".</summary>
    private static readonly HashSet<int> DeviceLoss = new()
    {
        2, 3, 21, 55, 53, 59, 64, 67, 121, 483, 1117, 1167, 1231, 1232, 1219, 1326, 1396, 1450, 2250,
    };

    private sealed record FileItem(string Src, string Dst, long Size);

    private sealed class Plan
    {
        public List<string> Dirs { get; } = new();
        public List<string> SourceDirs { get; } = new();
        public List<FileItem> Files { get; } = new();
    }

    /// <summary>Raised for problems that make every remaining file pointless (the destination is full).</summary>
    private sealed class JobAbortException(string message) : Exception(message);

    public static async Task RunAsync(CopyJob job)
    {
        var ct = job.Cts.Token;
        using var stopSampler = new CancellationTokenSource();
        Task sampler = Task.CompletedTask;
        try
        {
            job.State = JobState.Scanning;
            job.StartedUtc = DateTime.UtcNow;
            var plan = await Task.Run(() => Scan(job, ct), ct);
            if (job.FatalError != null) { job.State = JobState.Failed; return; }
            if (CheckSpace(job) is { } problem) { job.FatalError = problem; job.State = JobState.Failed; return; }

            job.State = JobState.Running;
            job.StartedUtc = DateTime.UtcNow;
            sampler = Sample(job, stopSampler.Token);
            await Task.Run(() => Execute(job, plan, ct), ct);

            job.State = job.FailedFiles == 0 ? JobState.Done : JobState.DoneWithErrors;
        }
        catch (Exception ex)
        {
            var real = Unwrap(ex);
            if (real is OperationCanceledException) job.State = JobState.Cancelled;
            else
            {
                job.FatalError = real is JobAbortException or InvalidOperationException ? real.Message : Describe(real);
                job.State = JobState.Failed;
                if (real is not JobAbortException and not InvalidOperationException) Log.Write($"Transfer failed: {job.Title}", real);
            }
        }
        finally
        {
            job.FinishedUtc = DateTime.UtcNow;
            if (job.SpeedHistory.Length < 2 && job.State is JobState.Done or JobState.DoneWithErrors)
            {
                double seconds = Math.Max((job.FinishedUtc - job.StartedUtc).TotalSeconds, 0.001);
                job.PushSpeed(job.TotalBytes / seconds); // too quick to have been sampled: show its average
                job.PushSpeed(job.TotalBytes / seconds);
            }
            stopSampler.Cancel();
            try { await sampler; } catch (OperationCanceledException) { }
            job.Speed = 0;
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        if (ex is not AggregateException agg) return ex;
        var inner = agg.Flatten().InnerExceptions;
        return inner.FirstOrDefault(e => e is not OperationCanceledException) ?? inner.FirstOrDefault() ?? ex;
    }

    // ---- scanning ---------------------------------------------------------------------------------------------

    private static Plan Scan(CopyJob job, CancellationToken ct)
    {
        var plan = new Plan();
        string dest = Path.GetFullPath(job.Destination);
        long bytes = 0;

        if (File.Exists(PathUtil.Long(dest)))
            throw new InvalidOperationException($"The destination \"{dest}\" is a file, not a folder.");

        foreach (string raw in job.Sources)
        {
            ct.ThrowIfCancellationRequested();
            string src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            string name = Path.GetFileName(src);
            if (name.Length == 0) name = src.Replace(":", "").Replace("\\", ""); // a drive root such as C:\

            try
            {
                if (File.Exists(PathUtil.Long(src)))
                {
                    long size = new FileInfo(PathUtil.Long(src)).Length;
                    plan.Files.Add(new FileItem(src, Path.Combine(dest, name), size));
                    bytes += size;
                }
                else if (Directory.Exists(PathUtil.Long(src)))
                {
                    if (PathUtil.IsInside(dest, src))
                        throw new InvalidOperationException($"Can't copy \"{name}\" into itself.");

                    string root = Path.Combine(dest, name);
                    plan.Dirs.Add(root);
                    plan.SourceDirs.Add(src);
                    foreach (var (path, isDir, length) in Walk(src))
                    {
                        string target = Path.Combine(root, Path.GetRelativePath(src, path));
                        if (isDir) { plan.Dirs.Add(target); plan.SourceDirs.Add(path); }
                        else { plan.Files.Add(new FileItem(path, target, length)); bytes += length; }
                        if (plan.Files.Count % 512 == 0) { job.SetTotals(plan.Files.Count, bytes); ct.ThrowIfCancellationRequested(); }
                    }
                }
                else if (!job.Options.Resume || !RootExists(src)) job.Fail(src, "Not found. It may have been moved, renamed or its drive disconnected.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                job.Fail(src, Describe(ex));
            }
            job.SetTotals(plan.Files.Count, bytes);
        }

        job.SetTotals(plan.Files.Count, bytes);
        if (plan.Files.Count == 0 && plan.Dirs.Count == 0 && job.FailedFiles > 0)
            job.FatalError = "None of the selected items could be read.";
        return plan;
    }

    /// <summary>Every file and folder under <paramref name="root"/>, hidden and system ones included, without following links.</summary>
    private static IEnumerable<(string Path, bool IsDir, long Length)> Walk(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        return new FileSystemEnumerable<(string, bool, long)>(PathUtil.Long(root),
            (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length), options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) => e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
    }

    /// <summary>Stops a copy that cannot fit before it writes a byte. Overwrite and skip can reuse space, so they are left to run.</summary>
    private static string? CheckSpace(CopyJob job)
    {
        if (job.Options.Collision != CollisionPolicy.KeepBoth) return null;
        try
        {
            string dest = Path.GetFullPath(job.Destination);
            if (PathUtil.IsNetwork(dest)) return null;
            if (job.Options.Mode == TransferMode.Move && job.Sources.All(s => PathUtil.SameVolume(Path.GetFullPath(s), dest))) return null;

            string? root = Path.GetPathRoot(dest);
            if (string.IsNullOrEmpty(root)) return null;
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < job.TotalBytes)
                return $"Not enough space on {root}: {Format.Bytes(job.TotalBytes)} needed, {Format.Bytes(free)} free.";
        }
        catch { /* cannot tell: let the copy find out */ }
        return null;
    }

    // ---- running ----------------------------------------------------------------------------------------------

    private static void Execute(CopyJob job, Plan plan, CancellationToken ct)
    {
        for (int i = 0; i < plan.Dirs.Count; i++)
        {
            string dir = plan.Dirs[i];
            ct.ThrowIfCancellationRequested();
            if (job.IsExcluded(plan.SourceDirs[i])) continue; // removed before its folder was even created
            try { Reconnecting(job, plan.Files.FirstOrDefault()?.Src ?? job.Sources[0], dir, ct, () => Directory.CreateDirectory(PathUtil.Long(dir))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Fail(dir, Describe(ex)); }
        }

        var small = plan.Files.Where(f => f.Size < SmallFile).ToList();
        var large = plan.Files.Where(f => f.Size >= SmallFile).OrderBy(f => f.Size).ToList();

        int threads = job.Options.Threads > 0 ? job.Options.Threads : AutoThreads(small);
        if (threads <= 1)
            foreach (var file in small) ProcessFile(job, file, ct);
        else
            Parallel.ForEach(small, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, file => ProcessFile(job, file, ct));
        foreach (var file in large) ProcessFile(job, file, ct);

        if (job.Options.Mode == TransferMode.Move) RemoveEmptySourceDirs(plan);
    }

    /// <summary>
    /// "Auto": many small files overlap well on solid-state drives and over a network (where each file costs a round trip),
    /// but make a spinning disk seek back and forth, so those stay one by one.
    /// </summary>
    private static int AutoThreads(List<FileItem> small)
    {
        if (small.Count < 8) return 1;
        string src = small[0].Src, dst = small[0].Dst;
        if (PathUtil.IsNetwork(src) || PathUtil.IsNetwork(dst)) return 4;
        return DriveKind.IsSolidState(src) && DriveKind.IsSolidState(dst) ? 4 : 1;
    }

    private static void ProcessFile(CopyJob job, FileItem file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        job.Gate.Wait(ct);

        bool verify = job.Options.Verify;
        bool move = job.Options.Mode == TransferMode.Move;
        long fileWork = file.Size * (verify ? 2 : 1);
        long counted = 0;
        void Count(long bytes) { Interlocked.Add(ref counted, bytes); job.AddWork(bytes); }

        if (job.IsExcluded(file.Src)) // the user removed this item from the list
        {
            job.AddSkipped();
            job.AddWork(fileWork);
            return;
        }
        job.CurrentFile = file.Src;

        // Removing an item (or "skip this file") cancels just this file's token, not the whole job's.
        using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fct = fileCts.Token;
        job.Register(file.Src, fileCts);
        string? written = null;

        try
        {
            for (int name = 0; ; name++)
            {
                var (copy, dst, overwrite) = Resolve(job.Options.Collision, file, job.Options.Resume);
                if (!copy) { job.AddSkipped(); return; }

                try
                {
                    // One attempt covers creating the folder, copying and verifying, so a drive that drops out at any
                    // point restarts the whole file once it is back; the retry reuses the (reserved) name.
                    int attempt = 0;
                    written = dst;
                    Reconnecting(job, file.Src, dst, fct, () =>
                    {
                        Count(-Interlocked.Read(ref counted)); // a retry recounts the file from its first byte
                        bool retry = attempt++ > 0;
                        Directory.CreateDirectory(PathUtil.Long(Path.GetDirectoryName(dst)!));
                        if (overwrite || retry) ClearReadOnly(dst);

                        if (move && PathUtil.SameVolume(file.Src, dst))
                        {
                            // Same volume: a rename, no data moves at all.
                            File.Move(PathUtil.Long(file.Src), PathUtil.Long(dst), overwrite || retry);
                            Count(fileWork);
                            return;
                        }

                        bool big = file.Size >= NativeCopy.UnbufferedThreshold;
                        bool local = !PathUtil.IsNetwork(file.Src) && !PathUtil.IsNetwork(dst);
                        // Medium and large files leave a resumable partial file if the drive drops out.
                        NativeCopy.CopyFile(file.Src, dst, overwrite || retry, big && local, file.Size >= RestartableFrom, Count, fct, job.Gate);
                        if (verify) Verify(job, file.Src, dst, Count, fct);
                    });

                    if (move && File.Exists(PathUtil.Long(file.Src))) // a same-volume move already renamed it away
                    {
                        ClearReadOnly(file.Src);
                        File.Delete(PathUtil.Long(file.Src));
                    }
                    job.AddDone();
                    return;
                }
                catch (IOException ex) when (NativeCopy.IsAlreadyExists(ex) && !overwrite && name < 5)
                {
                    // Another worker took the name between the check and the copy: pick again.
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only this file was cancelled: the user removed it. Count it as skipped and take back what was written.
            job.AddSkipped();
            if (written != null) { try { File.Delete(PathUtil.Long(written)); } catch { /* nothing left to remove */ } }
        }
        catch (OperationCanceledException) { throw; }
        catch (JobAbortException) { throw; }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new JobAbortException($"The destination ran out of space while copying \"{Path.GetFileName(file.Src)}\".");
        }
        catch (Exception ex)
        {
            job.Fail(file.Src, Describe(ex));
            Log.Write($"Could not copy {file.Src} -> {file.Dst}", ex);
        }
        finally
        {
            job.Unregister(file.Src);
            long delta = fileWork - Interlocked.Read(ref counted);
            if (delta != 0) job.AddWork(delta); // keep the bar honest after a skip or failure
        }
    }

    // ---- disconnects ------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="action"/>; if it fails because a drive or share vanished, waits until both ends are back and
    /// runs it again, for as long as it takes (cancel ends the wait). Locked files get a few short retries.
    /// </summary>
    private static void Reconnecting(CopyJob job, string src, string dst, CancellationToken ct, Action action)
    {
        int locked = 0, flaky = 0;
        while (true)
        {
            try { action(); return; }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) when (NativeCopy.IsSharingViolation(ex) && locked++ < 3)
            {
                ct.WaitHandle.WaitOne(250 * locked);
                ct.ThrowIfCancellationRequested();
            }
            catch (IOException ex) when (!NativeCopy.IsAlreadyExists(ex) && !IsDiskFull(ex) && LostDevice(ex, src, dst, out bool rootGone) && (rootGone || flaky++ < 5))
            {
                WaitForDevices(job, src, dst, ct);
            }
        }
    }

    /// <summary>True if the error looks like a disconnect; <paramref name="rootGone"/> says a drive/share is missing right now.</summary>
    private static bool LostDevice(IOException ex, string src, string dst, out bool rootGone)
    {
        rootGone = !RootExists(src) || !RootExists(dst);
        return rootGone || DeviceLoss.Contains(NativeCopy.Win32Code(ex));
    }

    private static bool RootExists(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch { return false; }
    }

    private static void WaitForDevices(CopyJob job, string src, string dst, CancellationToken ct)
    {
        string Missing()
        {
            if (!RootExists(src)) return $"{Path.GetPathRoot(Path.GetFullPath(src))} (source) was disconnected";
            if (!RootExists(dst)) return $"{Path.GetPathRoot(Path.GetFullPath(dst))} (destination) was disconnected";
            return "The connection was interrupted";
        }

        job.EnterWait(Missing());
        Log.Write($"Waiting for a device: {Missing()}");
        try
        {
            do
            {
                ct.WaitHandle.WaitOne(1000);
                ct.ThrowIfCancellationRequested();
            }
            while (!RootExists(src) || !RootExists(dst) || !SafeExists(src));
        }
        finally { job.LeaveWait(); }
        Log.Write("Device is back, continuing");
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(PathUtil.Long(path)) || Directory.Exists(PathUtil.Long(path)); }
        catch { return false; }
    }

    // ---- collisions, errors ---------------------------------------------------------------------------------

    /// <summary>Decides what happens when the destination file may already exist.</summary>
    private static (bool Copy, string Dst, bool Overwrite) Resolve(CollisionPolicy policy, FileItem file, bool resume)
    {
        string dst = file.Dst;
        if (!PathUtil.Exists(dst)) return (true, dst, false);

        if (resume && File.Exists(PathUtil.Long(dst)))
        {
            // Carrying on an earlier run: same size and timestamp means it was copied intact; anything else is a half-copied file to finish.
            var info = new FileInfo(PathUtil.Long(dst));
            bool intact = info.Length == file.Size
                && Math.Abs((info.LastWriteTimeUtc - File.GetLastWriteTimeUtc(PathUtil.Long(file.Src))).TotalSeconds) < 2;
            return intact ? (false, dst, false) : (true, dst, true);
        }

        if (PathUtil.SamePath(file.Src, dst) && policy != CollisionPolicy.KeepBoth) return (false, dst, false);
        bool dstIsDir = Directory.Exists(PathUtil.Long(dst));

        switch (policy)
        {
            case CollisionPolicy.Skip:
                return (false, dst, false);
            case CollisionPolicy.Overwrite when !dstIsDir:
                return (true, dst, true);
            case CollisionPolicy.OverwriteOlder when !dstIsDir:
                bool newer = File.GetLastWriteTimeUtc(PathUtil.Long(file.Src)) > File.GetLastWriteTimeUtc(PathUtil.Long(dst));
                return newer ? (true, dst, true) : (false, dst, false);
            default:
                return (true, PathUtil.UniqueName(dst), false);
        }
    }

    private static bool IsDiskFull(IOException ex) => NativeCopy.Win32Code(ex) is ErrorDiskFull or ErrorHandleDiskFull;

    /// <summary>Plain-language version of a failure, for the error list.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied. Check the permissions, or whether another program has the file open.",
        PathTooLongException => "The path is too long.",
        DirectoryNotFoundException => "A folder on the path no longer exists. A drive may have been disconnected.",
        FileNotFoundException => "The file no longer exists. It may have been moved or deleted while copying.",
        IOException io => NativeCopy.Win32Code(io) switch
        {
            ErrorDiskFull or ErrorHandleDiskFull => "The destination is full.",
            32 or 33 => "The file is in use by another program.",
            123 or 161 => "The name contains characters this drive does not allow.",
            206 => "The path is too long.",
            19 => "The destination is write-protected.",
            23 => "Data error: the drive could not read the file (CRC). The disk may be failing.",
            1920 => "The file cannot be accessed by the system (a cloud placeholder or protected file).",
            int c when DeviceLoss.Contains(c) => "The drive or network share was disconnected.",
            _ => io.Message,
        },
        _ => ex.Message,
    };

    private static void ClearReadOnly(string path)
    {
        try
        {
            string p = PathUtil.Long(path);
            var attrs = File.GetAttributes(p);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, attrs & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing there to clear */ }
    }

    /// <summary>
    /// Reads the source and the copy at the same time, in big chunks, and compares every byte; a bad copy is deleted and reported.
    /// Reading both together takes about as long as reading one, so verifying costs far less than a second full pass.
    /// </summary>
    private static void Verify(CopyJob job, string src, string dst, Action<long> count, CancellationToken ct)
    {
        bool same;
        using (var a = File.OpenHandle(PathUtil.Long(src), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var b = File.OpenHandle(PathUtil.Long(dst), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            long length = RandomAccess.GetLength(a);
            same = length == RandomAccess.GetLength(b);
            var bufA = new byte[VerifyChunk];
            var bufB = new byte[VerifyChunk];

            for (long offset = 0; same && offset < length;)
            {
                job.Gate.Wait(ct);
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(length - offset, VerifyChunk);
                var readA = ReadFullAsync(a, bufA, n, offset);
                var readB = ReadFullAsync(b, bufB, n, offset);
                Task.WaitAll(readA, readB);
                same = readA.Result == n && readB.Result == n && bufA.AsSpan(0, n).SequenceEqual(bufB.AsSpan(0, n));
                offset += n;
                count(n);
            }
        }

        if (same) return;
        try { File.Delete(PathUtil.Long(dst)); } catch { /* reported below either way */ }
        throw new InvalidDataException("Verification failed: the copy does not match the original, so it was deleted.");
    }

    private static async Task<int> ReadFullAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, byte[] buffer, int count, long offset)
    {
        int total = 0;
        while (total < count)
        {
            int read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(total, count - total), offset + total);
            if (read == 0) break; // the file is shorter than it claimed
            total += read;
        }
        return total;
    }

    private static void RemoveEmptySourceDirs(Plan plan)
    {
        foreach (string dir in plan.SourceDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                string p = PathUtil.Long(dir);
                if (!Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leave a folder we cannot remove */ }
        }
    }

    // ---- speed ------------------------------------------------------------------------------------------------

    private static async Task Sample(CopyJob job, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        long last = job.DoneWork;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (await timer.WaitForNextTickAsync(stop))
        {
            double seconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            long now = job.DoneWork;
            double instant = Math.Max(0, now - last) / Math.Max(seconds, 0.001);
            last = now;
            job.Speed = job.State == JobState.Paused || job.DeviceWait != null ? 0 : job.Speed <= 0 ? instant : job.Speed * 0.6 + instant * 0.4;
            job.PushSpeed(job.Speed);
        }
    }
}
