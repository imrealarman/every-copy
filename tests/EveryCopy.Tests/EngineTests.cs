using EveryCopy.Models;
using EveryCopy.Services;

namespace EveryCopy.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "everycopy-tests-" + Guid.NewGuid().ToString("N"));

    public EngineTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { /* temp folder, the OS will clean it eventually */ }
    }

    private string P(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private string Write(string relative, byte[] data)
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static async Task<CopyJob> Run(string[] sources, string dest, TransferMode mode = TransferMode.Copy,
        CollisionPolicy policy = CollisionPolicy.KeepBoth, bool verify = false)
    {
        var job = new CopyJob(sources, dest, new JobOptions(mode, policy, verify, 4));
        await CopyEngine.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task Copies_a_folder_tree_with_identical_content()
    {
        var a = Bytes(1000, 1); var b = Bytes(5_000_000, 2); var c = Bytes(0, 3);
        Write(@"src\photos\a.bin", a);
        Write(@"src\photos\deep\er\b.bin", b);
        Write(@"src\photos\empty.bin", c);
        Directory.CreateDirectory(P(@"src\photos\emptydir"));

        var job = await Run(new[] { P(@"src\photos") }, P("dst"));

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(3, job.DoneFiles);
        Assert.Equal(a, File.ReadAllBytes(P(@"dst\photos\a.bin")));
        Assert.Equal(b, File.ReadAllBytes(P(@"dst\photos\deep\er\b.bin")));
        Assert.Empty(File.ReadAllBytes(P(@"dst\photos\empty.bin")));
        Assert.True(Directory.Exists(P(@"dst\photos\emptydir")));
        Assert.Equal(job.TotalWork, job.DoneWork);
    }

    [Fact]
    public async Task Copies_single_files_and_keeps_the_timestamp()
    {
        string f = Write(@"src\one.txt", Bytes(10, 4));
        File.SetLastWriteTimeUtc(f, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var job = await Run(new[] { f }, P("dst"));

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(P(@"dst\one.txt")));
    }

    [Fact]
    public async Task Verify_counts_both_passes_and_succeeds()
    {
        Write(@"src\v.bin", Bytes(3_000_000, 5));

        var job = await Run(new[] { P(@"src\v.bin") }, P("dst"), verify: true);

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(6_000_000, job.TotalWork);
        Assert.Equal(6_000_000, job.DoneWork);
    }

    [Fact]
    public async Task Keep_both_renames_the_newcomer()
    {
        Write(@"src\a.txt", new byte[] { 1 });
        Write(@"dst\a.txt", new byte[] { 9 });

        var job = await Run(new[] { P(@"src\a.txt") }, P("dst"));

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(P(@"dst\a.txt")));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(P(@"dst\a (2).txt")));
        Assert.Equal(JobState.Done, job.State);
    }

    [Fact]
    public async Task Overwrite_replaces_even_a_read_only_file()
    {
        Write(@"src\a.txt", new byte[] { 1 });
        string existing = Write(@"dst\a.txt", new byte[] { 9 });
        File.SetAttributes(existing, FileAttributes.ReadOnly);

        var job = await Run(new[] { P(@"src\a.txt") }, P("dst"), policy: CollisionPolicy.Overwrite);

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(existing));
    }

    [Fact]
    public async Task Skip_leaves_the_existing_file_and_counts_it()
    {
        Write(@"src\a.txt", new byte[] { 1 });
        Write(@"dst\a.txt", new byte[] { 9 });

        var job = await Run(new[] { P(@"src\a.txt") }, P("dst"), policy: CollisionPolicy.Skip);

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(P(@"dst\a.txt")));
        Assert.Equal(1, job.SkippedFiles);
        Assert.Equal(job.TotalWork, job.DoneWork);
    }

    [Fact]
    public async Task Overwrite_older_only_replaces_older_files()
    {
        string oldSrc = Write(@"src\old.txt", new byte[] { 1 });
        string newSrc = Write(@"src\new.txt", new byte[] { 2 });
        string oldDst = Write(@"dst\old.txt", new byte[] { 9 });
        string newDst = Write(@"dst\new.txt", new byte[] { 9 });
        File.SetLastWriteTimeUtc(oldSrc, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(oldDst, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newSrc, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newDst, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Run(new[] { oldSrc, newSrc }, P("dst"), policy: CollisionPolicy.OverwriteOlder);

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(oldDst)); // destination was newer: kept
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(newDst)); // source was newer: replaced
    }

    [Fact]
    public async Task Copying_two_same_named_files_into_one_folder_keeps_both()
    {
        Write(@"src1\x.txt", new byte[] { 1 });
        Write(@"src2\x.txt", new byte[] { 2 });

        var job = await Run(new[] { P(@"src1\x.txt"), P(@"src2\x.txt") }, P("dst"));

        Assert.Equal(JobState.Done, job.State);
        var contents = Directory.GetFiles(P("dst")).Select(f => File.ReadAllBytes(f)[0]).OrderBy(x => x).ToArray();
        Assert.Equal(new byte[] { 1, 2 }, contents);
    }

    [Fact]
    public async Task Move_removes_the_source_files_and_folders()
    {
        Write(@"src\tree\a.txt", Bytes(100, 6));
        Write(@"src\tree\sub\b.txt", Bytes(100, 7));

        var job = await Run(new[] { P(@"src\tree") }, P("dst"), TransferMode.Move);

        Assert.Equal(JobState.Done, job.State);
        Assert.False(Directory.Exists(P(@"src\tree")));
        Assert.True(File.Exists(P(@"dst\tree\sub\b.txt")));
    }

    [Fact]
    public async Task Move_with_verify_still_removes_the_source()
    {
        // Same volume moves are renames, so this exercises the verify path only when volumes differ; here it
        // checks that the rename path keeps the bar and counters consistent.
        Write(@"src\a.bin", Bytes(2_000_000, 8));

        var job = await Run(new[] { P(@"src\a.bin") }, P("dst"), TransferMode.Move, verify: true);

        Assert.Equal(JobState.Done, job.State);
        Assert.False(File.Exists(P(@"src\a.bin")));
        Assert.Equal(job.TotalWork, job.DoneWork);
    }

    [Fact]
    public async Task A_folder_cannot_be_copied_into_itself()
    {
        Write(@"src\a.txt", new byte[] { 1 });

        var job = await Run(new[] { P("src") }, P(@"src\inside"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("into itself", job.FatalError);
    }

    [Fact]
    public async Task A_missing_source_is_reported_without_stopping_the_rest()
    {
        string good = Write(@"src\good.txt", new byte[] { 1 });

        var job = await Run(new[] { P(@"src\missing.txt"), good }, P("dst"));

        Assert.Equal(JobState.DoneWithErrors, job.State);
        Assert.Equal(1, job.DoneFiles);
        Assert.Single(job.Failures);
    }

    [Fact]
    public async Task Cancel_stops_a_running_job_and_leaves_no_partial_file()
    {
        Write(@"src\big.bin", Bytes(64_000_000, 9));
        var job = new CopyJob(new[] { P(@"src\big.bin") }, P("dst"), new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 2));

        job.Pause(); // no effect before it runs
        var run = CopyEngine.RunAsync(job);
        job.Cancel();
        await run;

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.False(File.Exists(P(@"dst\big.bin")));
    }

    [Fact]
    public async Task Pause_holds_progress_until_resumed()
    {
        for (int i = 0; i < 40; i++) Write($@"src\f{i}.bin", Bytes(300_000, i));
        var job = new CopyJob(new[] { P("src") }, P("dst"), new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 1));
        var run = CopyEngine.RunAsync(job);

        while (job.State != JobState.Running) await Task.Delay(1);
        job.Pause();
        await Task.Delay(300);
        long frozen = job.DoneWork;
        await Task.Delay(400);
        Assert.Equal(frozen, job.DoneWork);

        job.Resume();
        await run;
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(40, job.DoneFiles);
    }

    [Fact]
    public async Task Queue_runs_jobs_in_order()
    {
        Write(@"src\a.txt", new byte[] { 1 });
        Write(@"src\b.txt", new byte[] { 2 });
        var queue = new CopyQueue();
        var options = new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 2);

        var first = queue.Enqueue(new[] { P(@"src\a.txt") }, P("dst"), options);
        var second = queue.Enqueue(new[] { P(@"src\b.txt") }, P("dst"), options);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(first.IsFinished && second.IsFinished) && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.Equal(JobState.Done, first.State);
        Assert.Equal(JobState.Done, second.State);
        Assert.True(first.FinishedUtc <= second.StartedUtc);
    }

    [Fact]
    public void Long_paths_get_the_extended_prefix_only_when_needed()
    {
        Assert.Equal(@"C:\short\file.txt", PathUtil.Long(@"C:\short\file.txt"));
        string longPath = @"C:\" + new string('a', 250) + @"\f.txt";
        Assert.StartsWith(@"\\?\C:\", PathUtil.Long(longPath));
        Assert.StartsWith(@"\\?\UNC\srv\share", PathUtil.Long(@"\\srv\share\" + new string('b', 250)));
    }

    [Fact]
    public async Task Copies_a_file_whose_path_is_over_260_characters()
    {
        string deep = P(string.Join(@"\", Enumerable.Repeat(new string('d', 60), 5)));
        Directory.CreateDirectory(PathUtil.Long(deep));
        string file = Path.Combine(deep, "long.txt");
        Assert.True(file.Length > 260);
        File.WriteAllBytes(PathUtil.Long(file), new byte[] { 7, 7 });

        var job = await Run(new[] { file }, P("dst"));

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(P(@"dst\long.txt")));
    }

    [Fact]
    public void Launch_arguments_are_parsed()
    {
        var r = LaunchRequest.Parse(new[] { @"C:\a b", "--to", @"D:\out", "--move", "--start", "--background", @"C:\c" });
        Assert.Equal(new[] { @"C:\a b", @"C:\c" }, r.Paths);
        Assert.Equal(@"D:\out", r.Destination);
        Assert.True(r.Move);
        Assert.True(r.Start);

        var plain = LaunchRequest.Parse(new[] { @"C:\only" });
        Assert.Null(plain.Destination);
        Assert.False(plain.Move);
        Assert.False(plain.Start);
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_damaged_file()
    {
        string file = P("settings.json");
        new AppSettings { Theme = "dark", Verify = true, Collision = CollisionPolicy.Skip, Threads = 3, LastDestination = @"D:\x" }.Save(file);
        var loaded = AppSettings.Load(file);
        Assert.Equal("dark", loaded.Theme);
        Assert.True(loaded.Verify);
        Assert.Equal(CollisionPolicy.Skip, loaded.Collision);
        Assert.Equal(3, loaded.Threads);

        File.WriteAllText(file, "{ not json");
        Assert.Equal("system", AppSettings.Load(file).Theme);
    }

    [Fact]
    public void Sizes_and_durations_read_naturally()
    {
        Assert.Equal("512 B", Format.Bytes(512));
        Assert.Equal("1.5 MB", Format.Bytes(1_572_864));
        Assert.Equal("0:09", Format.Duration(TimeSpan.FromSeconds(9)));
        Assert.Equal("1:01:05", Format.Duration(TimeSpan.FromSeconds(3665)));
    }

    [Fact]
    public void Windows_copy_option_copies_and_moves_with_the_shell()
    {
        string a = Write(Path.Combine("src", "a.txt"), new byte[] { 1, 2 });
        string b = Write(Path.Combine("src", "b.txt"), new byte[] { 3 });
        Directory.CreateDirectory(P("dst"));

        Assert.Null(NativeShell.Transfer(new[] { a, b }, P("dst"), move: false, IntPtr.Zero, silent: true));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(P("dst", "a.txt")));
        Assert.True(File.Exists(a));

        Directory.CreateDirectory(P("moved"));
        Assert.Null(NativeShell.Transfer(new[] { b }, P("moved"), move: true, IntPtr.Zero, silent: true));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(P("moved", "b.txt")));
    }

    [Fact]
    public void Settings_ask_which_copier_on_first_run_and_default_to_the_app()
    {
        var fresh = new AppSettings();
        Assert.True(fresh.AskWhichCopier);
        Assert.False(fresh.UseWindowsCopy);
    }

    [Fact]
    public async Task Unicode_names_copy_verify_and_move_correctly()
    {
        string[] names = { "فایل فارسی.txt", "中文文件.txt", "Ünïcödé ñame.txt", "emoji 😀 file.txt", "Привет.txt" };
        var data = names.Select((n, i) => Bytes(5000, 100 + i)).ToArray();
        for (int i = 0; i < names.Length; i++) Write(Path.Combine("منبع", "پوشه 文件夹", names[i]), data[i]);

        var job = await Run(new[] { P("منبع") }, P("مقصد 目的地"), verify: true);

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(0, job.FailedFiles);
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(data[i], File.ReadAllBytes(P("مقصد 目的地", "منبع", "پوشه 文件夹", names[i])));
        Assert.Equal("Copy منبع", job.Title);

        var moved = await Run(new[] { P("مقصد 目的地", "منبع") }, P("moved ✓"), TransferMode.Move);
        Assert.Equal(JobState.Done, moved.State);
        Assert.False(Directory.Exists(P("مقصد 目的地", "منبع")));
        Assert.Equal(names.Length, Directory.GetFiles(P("moved ✓", "منبع", "پوشه 文件夹")).Length);
    }

    [Fact]
    public void Unicode_survives_the_launch_arguments_and_settings()
    {
        var request = LaunchRequest.Parse(new[] { @"D:\دانلود\فایل 😀.zip", "--to", @"E:\目的地" });
        Assert.Equal(@"D:\دانلود\فایل 😀.zip", request.Paths[0]);
        Assert.Equal(@"E:\目的地", request.Destination);

        string file = P("settings.json");
        new AppSettings { LastDestination = @"D:\پوشه 文件夹" }.Save(file);
        Assert.Equal(@"D:\پوشه 文件夹", AppSettings.Load(file).LastDestination);
    }

    [Fact]
    public async Task A_removed_item_is_not_copied_but_the_rest_is()
    {
        Write(@"src\keep.txt", new byte[] { 1 });
        Write(@"src\drop\a.txt", new byte[] { 2 });
        Write(@"src\drop\deep\b.txt", new byte[] { 3 });
        var job = new CopyJob(new[] { P(@"src\keep.txt"), P(@"src\drop") }, P("dst"), new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 1));

        job.Remove(job.Items[1]);
        await CopyEngine.RunAsync(job);

        Assert.Equal(JobState.Done, job.State);
        Assert.True(File.Exists(P(@"dst\keep.txt")));
        Assert.False(Directory.Exists(P(@"dst\drop")));
        Assert.Equal(2, job.SkippedFiles);
        Assert.Contains("1 of 2", job.ItemsHeader);
        Assert.Equal(job.TotalWork, job.DoneWork);
    }

    [Fact]
    public async Task Removing_an_item_while_the_job_is_held_skips_it_and_cleans_up()
    {
        Write(@"src\big.bin", Bytes(64_000_000, 11));
        Write(@"src\small.txt", new byte[] { 5 });
        var job = new CopyJob(new[] { P(@"src\big.bin"), P(@"src\small.txt") }, P("dst"), new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 1));
        job.Gate.Reset();                  // holds the job before its first file, so the removal always lands first
        var run = CopyEngine.RunAsync(job);

        while (job.State is JobState.Queued or JobState.Scanning) await Task.Delay(1);
        job.Remove(job.Items[0]);
        job.Gate.Set();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobState.Done, job.State);
        Assert.False(File.Exists(P(@"dst\big.bin")));
        Assert.True(File.Exists(P(@"dst\small.txt")));
        Assert.Equal(1, job.SkippedFiles);
    }

    [Fact]
    public void Skip_current_cancels_only_the_files_in_progress_and_keeps_the_items()
    {
        var job = new CopyJob(new[] { @"C:\a", @"C:\b" }, @"D:\out", new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 1));
        using var inProgress = new CancellationTokenSource();
        job.Register(@"C:\a\file.bin", inProgress);

        job.SkipCurrent();

        Assert.True(inProgress.IsCancellationRequested);
        Assert.False(job.Cts.IsCancellationRequested);   // the job itself carries on
        Assert.All(job.Items, item => Assert.False(item.Removed));
    }

    [Fact]
    public void Speed_history_keeps_the_last_minute()
    {
        var job = new CopyJob(new[] { @"C:\x" }, @"D:\y", new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 1));
        for (int i = 1; i <= 150; i++) job.PushSpeed(i * 1000);

        Assert.Equal(120, job.SpeedHistory.Length);
        Assert.Equal(31_000, job.SpeedHistory[0]);
        Assert.Equal(150_000, job.PeakSpeed);
    }

    [Fact]
    public async Task A_resumed_job_skips_intact_files_and_finishes_damaged_ones()
    {
        var a = Bytes(10_000, 20); var b = Bytes(10_000, 21); var c = Bytes(10_000, 22);
        Write(@"src\a.bin", a); Write(@"src\b.bin", b); Write(@"src\c.bin", c);
        await Run(new[] { P("src") }, P("dst"));

        // Simulate an interruption: b is cut short, c was never copied.
        File.WriteAllBytes(P(@"dst\src\b.bin"), b[..3000]);
        File.Delete(P(@"dst\src\c.bin"));

        var job = new CopyJob(new[] { P("src") }, P("dst"), new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 1, Resume: true));
        await CopyEngine.RunAsync(job);

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(1, job.SkippedFiles);               // a was already there, intact
        Assert.Equal(2, job.DoneFiles);                  // b finished, c copied
        Assert.Equal(b, File.ReadAllBytes(P(@"dst\src\b.bin")));
        Assert.Equal(c, File.ReadAllBytes(P(@"dst\src\c.bin")));
        Assert.Empty(Directory.GetFiles(P(@"dst\src"), "* (2).bin")); // no duplicate copies from "keep both"
    }

    [Fact]
    public async Task A_resumed_move_ignores_sources_that_were_already_moved()
    {
        Write(@"src\left.txt", new byte[] { 1 });
        var job = new CopyJob(new[] { P(@"src\gone.txt"), P(@"src\left.txt") }, P("dst"),
            new JobOptions(TransferMode.Move, CollisionPolicy.KeepBoth, false, 1, Resume: true));
        await CopyEngine.RunAsync(job);

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(0, job.FailedFiles);
    }

    [Fact]
    public void Unfinished_jobs_are_saved_and_finished_ones_are_not()
    {
        string file = P("queue.json");
        var options = new JobOptions(TransferMode.Move, CollisionPolicy.Skip, true, 0);
        var waiting = new CopyJob(new[] { @"D:\مصدر", @"D:\b.txt" }, @"E:\هدف", options);
        var done = new CopyJob(new[] { @"D:\x" }, @"E:\y", options) { State = JobState.Done };

        JobStore.Save(new[] { waiting, done }, file);
        var loaded = JobStore.Load(file);

        var one = Assert.Single(loaded);
        Assert.Equal(new[] { @"D:\مصدر", @"D:\b.txt" }, one.Sources);
        Assert.Equal(@"E:\هدف", one.Destination);
        Assert.Equal(TransferMode.Move, one.Mode);
        Assert.Equal(CollisionPolicy.Skip, one.Collision);
        Assert.True(one.Verify);
        Assert.Equal(0, one.Threads);

        JobStore.Save(new[] { done }, file);
        Assert.False(File.Exists(file));
        File.WriteAllText(file, "{ broken");
        Assert.Empty(JobStore.Load(file));
    }

    [Fact]
    public void History_keeps_newest_first_caps_its_size_and_survives_a_broken_file()
    {
        string file = P("history.json");
        var options = new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, true, 1);
        var job = new CopyJob(new[] { @"D:\مصدر" }, @"E:\هدف", options) { State = JobState.DoneWithErrors };
        job.StartedUtc = DateTime.UtcNow.AddSeconds(-5);
        job.FinishedUtc = DateTime.UtcNow;

        HistoryStore.Add(HistoryStore.From(job), file);
        HistoryStore.Add(HistoryStore.From(new CopyJob(new[] { @"D:\second" }, @"E:\y", options) { State = JobState.Done }), file);

        var all = HistoryStore.Load(file);
        Assert.Equal(2, all.Count);
        Assert.Equal("second", all[0].From);
        Assert.Equal("مصدر", all[1].From);
        Assert.Equal(JobState.DoneWithErrors, all[1].State);
        Assert.InRange(all[1].Seconds, 4, 6);

        for (int i = 0; i < HistoryStore.MaxEntries + 5; i++) HistoryStore.Add(all[0], file);
        Assert.Equal(HistoryStore.MaxEntries, HistoryStore.Load(file).Count);

        File.WriteAllText(file, "{ broken");
        Assert.Empty(HistoryStore.Load(file));
        HistoryStore.Clear(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Destination_colours_are_distinct_never_green_red_or_yellow_and_names_are_remembered()
    {
        Destinations.FilePath = P("destinations.json");
        Destinations.Reset();

        // No palette colour may look like a status colour: red, orange, yellow or green.
        foreach (var s in Destinations.Palette)
        {
            double hue = System.Drawing.Color.FromArgb(s.R, s.G, s.B).GetHue();
            Assert.False(hue < 75 || hue > 345 || (hue >= 75 && hue <= 170), $"{s.Name} (hue {hue:0}) reads as a status colour");
        }

        // Four drives filled at the same time get four different colours.
        var inUse = new List<string>();
        var colours = new HashSet<int>();
        foreach (string key in new[] { "vol:A", "vol:B", "vol:C", "vol:D" })
        {
            inUse.Add(key);
            Assert.True(colours.Add(Destinations.Assign(key, inUse)));
        }

        // A drive keeps its colour next time, and a name given to it comes back after a restart.
        int again = Destinations.Assign("vol:B", new[] { "vol:B" });
        Assert.Equal(Destinations.ColorOf("vol:B"), again);
        Destinations.SetName("vol:B", "  Sara's USB  ");
        Destinations.Reset();
        Assert.Equal("Sara's USB", Destinations.NameOf("vol:B"));
        Assert.Contains("Sara's USB", Destinations.Label("vol:B"));
        Assert.Equal(Destinations.Palette[Destinations.ColorOf("vol:C")].Name, Destinations.Label("vol:C"));

        // More drives than colours: they share rather than fail.
        var many = Enumerable.Range(0, Destinations.Palette.Length + 2).Select(i => "vol:X" + i).ToList();
        foreach (string key in many) Destinations.Assign(key, many.Take(many.IndexOf(key) + 1));
        Assert.All(many, key => Assert.InRange(Destinations.ColorOf(key), 0, Destinations.Palette.Length - 1));
    }

    [Fact]
    public async Task Waiting_jobs_can_be_reordered()
    {
        Write(@"src\big.bin", Bytes(8_000_000, 30));
        Write(@"src\b.txt", new byte[] { 1 });
        Write(@"src\c.txt", new byte[] { 2 });
        var queue = new CopyQueue();
        var options = new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, false, 1);

        var first = queue.Enqueue(new[] { P(@"src\big.bin") }, P("d1"), options);
        first.Gate.Reset();                             // keeps the queue busy while the others wait (it blocks before the first file)
        while (first.State == JobState.Queued) await Task.Delay(1);
        var b = queue.Enqueue(new[] { P(@"src\b.txt") }, P("d2"), options);
        var c = queue.Enqueue(new[] { P(@"src\c.txt") }, P("d3"), options);

        Assert.False(queue.Move(first, 1));             // the running job cannot be moved
        Assert.True(queue.Move(c, -1));
        Assert.Equal(new[] { first, c, b }, queue.Jobs.ToArray());

        first.Gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(first.IsFinished && b.IsFinished && c.IsFinished) && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.True(c.StartedUtc <= b.StartedUtc, "the moved job should have run first");
    }

    [Fact]
    public void Drive_kind_check_answers_without_throwing()
    {
        _ = DriveKind.IsSolidState(root);   // true or false depending on the machine; it must simply not fail
        Assert.False(DriveKind.IsSolidState(@"\\server\share\file"));
    }

    [Fact]
    public async Task Auto_threads_still_copies_many_small_files_correctly()
    {
        for (int i = 0; i < 60; i++) Write(Path.Combine("src", $"f{i}.bin"), Bytes(3000, i));
        var job = new CopyJob(new[] { P("src") }, P("dst"), new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, true, 0));
        await CopyEngine.RunAsync(job);

        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(60, Directory.GetFiles(P("dst", "src")).Length);
    }

    [Fact]
    public void Verify_catches_a_corrupted_copy_and_deletes_it()
    {
        var original = Bytes(9_000_000, 40);
        var damaged = (byte[])original.Clone();
        damaged[8_999_999] ^= 0xFF;                      // one flipped byte, in the last chunk
        string src = Write("a.bin", original), dst = Write("b.bin", damaged);
        var job = new CopyJob(new[] { src }, root, new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, true, 1));
        var verify = typeof(CopyEngine).GetMethod("Verify", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            verify.Invoke(null, new object[] { job, src, dst, new Action<long>(_ => { }), CancellationToken.None }));

        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.False(File.Exists(dst));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Verify_accepts_an_identical_copy_and_counts_every_byte()
    {
        var data = Bytes(9_000_001, 41);                 // not a multiple of the chunk size
        string src = Write("a.bin", data), dst = Write("b.bin", data);
        var job = new CopyJob(new[] { src }, root, new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, true, 1));
        var verify = typeof(CopyEngine).GetMethod("Verify", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        long counted = 0;

        verify.Invoke(null, new object[] { job, src, dst, new Action<long>(n => counted += n), CancellationToken.None });

        Assert.Equal(data.Length, counted);
        Assert.True(File.Exists(dst));
    }

    [Fact]
    public void Speed_test_measures_a_folder_and_leaves_nothing_behind()
    {
        double speed = Benchmark.MeasureWrite(P("target"), 32 * 1024 * 1024, CancellationToken.None);

        Assert.True(speed > 1_000_000);                  // faster than 1 MB/s on any real drive
        Assert.Empty(Directory.GetFileSystemEntries(P("target")));
    }

    [Fact]
    public void Paste_and_menu_flags_are_parsed()
    {
        var r = LaunchRequest.Parse(new[] { "--paste", @"D:\here" });
        Assert.True(r.Paste);
        Assert.Equal(@"D:\here", r.Destination);
        Assert.Empty(r.Paths);
    }

    [Fact]
    public void Context_menu_installs_and_removes_every_entry()
    {
        string testRoot = @"Software\EveryCopyTests\" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(ContextMenu.IsInstalled(testRoot));
            ContextMenu.Install(@"C:\Apps\EveryCopy\EveryCopy.exe", testRoot);
            Assert.True(ContextMenu.IsInstalled(testRoot));
            Assert.Equal(@"C:\Apps\EveryCopy\EveryCopy.exe", ContextMenu.InstalledExe(testRoot));

            using (var move = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testRoot + @"\Directory\shell\EveryCopy.Move\command"))
                Assert.Equal("\"C:\\Apps\\EveryCopy\\EveryCopy.exe\" --move \"%1\"", move!.GetValue("") as string);
            using (var paste = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testRoot + @"\Directory\Background\shell\EveryCopy.Paste\command"))
                Assert.Contains("--paste \"%V\"", paste!.GetValue("") as string);

            ContextMenu.Uninstall(testRoot);
            Assert.False(ContextMenu.IsInstalled(testRoot));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\EveryCopyTests", false); }
    }

    [Fact]
    public void Errors_are_described_in_plain_language()
    {
        Assert.Contains("Access denied", CopyEngine.Describe(new UnauthorizedAccessException()));
        Assert.Equal("The destination is full.", CopyEngine.Describe(new IOException("x", unchecked((int)0x80070070))));
        Assert.Equal("The file is in use by another program.", CopyEngine.Describe(new IOException("x", unchecked((int)0x80070020))));
        Assert.Contains("disconnected", CopyEngine.Describe(new IOException("x", unchecked((int)0x80070040))));
    }

    [Fact]
    public async Task A_destination_drive_that_disappears_is_waited_for_and_the_copy_finishes()
    {
        // A subst drive stands in for a USB stick or network share: removing it mid-copy is a real disconnect.
        string letter = Enumerable.Range('R', 'Z' - 'R' + 1).Select(c => ((char)c).ToString())
            .FirstOrDefault(l => !Directory.Exists(l + @":\")) ?? throw new InvalidOperationException("no free drive letter");
        string drive = letter + ":";
        string backing = P("device");
        Directory.CreateDirectory(backing);
        for (int i = 0; i < 1500; i++) Write(Path.Combine("src", $"file{i:0000}.bin"), Bytes(20_000, i));

        static void Subst(string args)
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("subst", args) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
        }

        Subst($"{drive} \"{backing}\"");
        try
        {
            var job = new CopyJob(new[] { P("src") }, drive + @"\out", new JobOptions(TransferMode.Copy, CollisionPolicy.KeepBoth, true, 1));
            var run = CopyEngine.RunAsync(job);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (job.DoneFiles < 100 && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(1);
            Assert.False(run.IsCompleted, "the copy finished before the drive could be pulled");

            Subst($"{drive} /d");                       // unplug
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (job.DeviceWait == null && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.NotNull(job.DeviceWait);
            Assert.Contains(drive, job.DeviceWait);
            Assert.Equal(JobState.Running, job.State);

            int frozen = job.DoneFiles;
            await Task.Delay(1500);
            Assert.Equal(frozen, job.DoneFiles);        // nothing moves while it is gone

            Subst($"{drive} \"{backing}\"");          // plug back in
            await run.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(JobState.Done, job.State);
            Assert.Equal(0, job.FailedFiles);
            Assert.Equal(1500, Directory.GetFiles(Path.Combine(backing, "out", "src")).Length);
            Assert.Equal(Bytes(20_000, 1499), File.ReadAllBytes(Path.Combine(backing, "out", "src", "file1499.bin")));
        }
        finally { Subst($"{drive} /d"); }
    }
}
