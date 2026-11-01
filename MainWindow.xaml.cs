using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EveryCopy.Models;
using EveryCopy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace EveryCopy;

public sealed partial class MainWindow : Window
{
    private static readonly int[] ThreadChoices = { 0, 1, 2, 4, 8, 16 }; // 0 = Auto
    private static readonly string[] WhenDoneChoices = { "nothing", "sleep", "shutdown" };

    private readonly AppSettings settings = AppSettings.Load();
    private readonly CopyQueue queue = new();
    private readonly ObservableCollection<SourceEntry> sources = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    private TransferMode mode = TransferMode.Copy;
    private bool loading = true;
    private bool closeConfirmed;
    private bool hiddenStart;
    private readonly TrayIcon? tray;
    private readonly PasteHook? pasteHook;
    private readonly MenuPasteHook? menuPasteHook;
    private TaskbarProgress? taskbar;
    private string savedSignature = "";
    private bool wasBusy;
    private readonly HashSet<CopyJob> recorded = new();
    private ProgressWindow? progressWindow;
    private bool offscreen; // a hidden start parks the window out of sight until it is really wanted

    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow(LaunchRequest first, bool background = false)
    {
        hiddenStart = background;
        InitializeComponent();
        Title = "EveryCopy";
        SourceList.ItemsSource = sources;
        JobList.ItemsSource = queue.Jobs;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        PlaceWindow();
        if (background)
        {
            offscreen = true; // the window must open once for its content to load; keep that out of sight
            AppWindow.Move(new PointInt32(-32000, -32000));
        }

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        try
        {
            if (File.Exists(Path.Combine(assets, "AppIcon.ico"))) AppWindow.SetIcon(Path.Combine(assets, "AppIcon.ico"));
            if (File.Exists(Path.Combine(assets, "AppIcon.png"))) TitleIcon.Source = new BitmapImage(new Uri(Path.Combine(assets, "AppIcon.png")));
        }
        catch (Exception ex) { Log.Write("Could not load the icon", ex); }

        // Restore what was chosen last time.
        DestBox.Text = settings.LastDestination;
        CollisionBox.SelectedIndex = (int)settings.Collision;
        ThreadsBox.SelectedIndex = Math.Max(0, Array.IndexOf(ThreadChoices, ThreadChoices.OrderBy(t => Math.Abs(t - settings.Threads)).First()));
        VerifyBox.IsChecked = settings.Verify;
        MenuBox.IsChecked = ContextMenu.IsInstalled();
        AskBox.IsChecked = settings.AskWhichCopier;
        DefaultBox.IsChecked = settings.UseAsDefault;
        WhenDoneBox.SelectedIndex = Math.Max(0, Array.IndexOf(WhenDoneChoices, settings.WhenDone));
        InterceptBox.IsChecked = settings.InterceptPaste;
        PopupBox.IsChecked = settings.ProgressPopup;
        try { StartupBox.IsChecked = Startup.IsEnabled(); } catch (Exception ex) { Log.Write("Could not read the startup entry", ex); }
        loading = false;

        // Closing the window leaves EveryCopy in the tray, where it keeps watching for Ctrl+V in Explorer.
        try
        {
            tray = new TrayIcon(Path.Combine(assets, "AppIcon.ico"), () => DispatcherQueue.TryEnqueue(BringToFront),
                () => settings.InterceptPaste, SetIntercept, () => DispatcherQueue.TryEnqueue(() => _ = RequestExitAsync()));
            pasteHook = new PasteHook(
                () => settings.InterceptPaste && (settings.AskWhichCopier || !settings.UseWindowsCopy),
                (window, desktop) => DispatcherQueue.TryEnqueue(() =>
                {
                    // Resolved here, outside the hook; if the folder is not a plain one, Explorer gets its Ctrl+V back.
                    string? folder = PasteHook.TextBoxFocused(window) ? null : PasteHook.ResolveFolder(window, desktop);
                    if (folder == null) PasteHook.PassThrough();
                    else Handle(new LaunchRequest(Array.Empty<string>(), folder, false, false, true));
                }));
            menuPasteHook = new MenuPasteHook(
                () => settings.InterceptPaste && (settings.AskWhichCopier || !settings.UseWindowsCopy),
                (window, desktop) => DispatcherQueue.TryEnqueue(() =>
                {
                    string? folder = PasteHook.ResolveFolder(window, desktop);
                    if (folder != null) Handle(new LaunchRequest(Array.Empty<string>(), folder, false, false, true));
                    else ShowHint("EveryCopy could not tell which folder this is. Use Ctrl+V there, or choose the destination here.");
                }));
        }
        catch (Exception ex) { Log.Write("Tray or Ctrl+V hook could not start", ex); }

        Root.ActualThemeChanged += (_, _) => ApplyThemeChrome();
        Root.Loaded += (_, _) =>
        {
            ApplyTheme();
            RefreshMenuIfStale();
            taskbar = new TaskbarProgress(WindowNative.GetWindowHandle(this));
            Handle(first);
            RestoreQueue();
            _ = FirstRunAsync();
        };

        sources.CollectionChanged += (_, _) => UpdateSources();
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (_, _) => Tick();
        timer.Start();

        AppWindow.Closing += OnClosing;
        UpdateSources();
        UpdateQueueChrome();
    }

    // ---- launch requests (command line, Explorer menus, second instance) ----------------------------------------

    public void Handle(LaunchRequest request)
    {
        bool windowOpen = AppWindow.IsVisible && !offscreen && !hiddenStart;
        bool ready = !string.IsNullOrWhiteSpace(request.Destination);
        // A paste or an Explorer "start" with the app closed gets the small progress window, not the whole app.
        bool popup = settings.ProgressPopup && !windowOpen && (request.Paste || (request.Start && request.Paths.Count > 0 && ready));

        if (request.Paste) sources.Clear(); // a paste is its own transfer, not an addition to a half-built one
        if (request.Move) SetMode(TransferMode.Move);
        foreach (string path in request.Paths) AddSource(path);
        if (!string.IsNullOrWhiteSpace(request.Destination)) DestBox.Text = request.Destination;

        bool nothingToDo = request.Paths.Count == 0 && !request.Paste;
        if (hiddenStart)
        {
            hiddenStart = false; // started with Windows, or for a popup: stay in the tray until something needs the window
            if (popup || nothingToDo) AppWindow.Hide();
            if (nothingToDo) return;
        }
        if (!popup) BringToFront();

        if (request.Paste) _ = PasteFromClipboard(popup);
        else if (request.Start && sources.Count > 0 && ready) _ = StartTransferAsync(popup);
    }

    private async Task PasteFromClipboard(bool popup)
    {
        try
        {
            var (paths, cut) = await Task.Run(ClipboardFiles.Read);
            if (paths.Count == 0) { ShowHint("There are no files on the clipboard to paste."); return; }

            SetMode(cut ? TransferMode.Move : TransferMode.Copy);
            foreach (string path in paths) AddSource(path);
            if (sources.Count > 0 && !string.IsNullOrWhiteSpace(DestBox.Text)) await StartTransferAsync(popup);
        }
        catch (Exception ex)
        {
            Log.Write("Paste failed", ex);
            ShowHint("The clipboard could not be read. Copy the files again, or drop them here.");
        }
    }

    /// <summary>Opens (or reuses) the small progress window for the transfers in the queue.</summary>
    private void ShowProgress()
    {
        try
        {
            if (progressWindow == null)
            {
                var window = new ProgressWindow(settings, queue, BringToFront);
                window.Closed += (_, _) => { if (progressWindow == window) progressWindow = null; };
                progressWindow = window;
            }
            progressWindow.Refresh();
            progressWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Write("Could not open the progress window", ex);
            progressWindow = null;
            BringToFront(); // better the whole app than no sign of progress
        }
    }

    private void BringToFront()
    {
        try
        {
            if (offscreen) { offscreen = false; PlaceWindow(); }
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
            AppWindow.Show();
            Activate();
        }
        catch (Exception ex) { Log.Write("Could not bring the window forward", ex); }
    }

    // ---- window, theme -----------------------------------------------------------------------------------------

    private void PlaceWindow()
    {
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(1080 * scale), h = (int)(720 * scale);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        w = Math.Min(w, work.Width); h = Math.Min(h, work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ThemeButton.Content = settings.Theme switch { "light" => "Theme: Light", "dark" => "Theme: Dark", _ => "Theme: System" };
        ApplyThemeChrome();
    }

    private void ApplyThemeChrome()
    {
        bool light = Root.ActualTheme == ElementTheme.Light;
        Palette.Apply(light);
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = light ? Colors.Black : Colors.White;
        bar.ButtonInactiveForegroundColor = Colors.Gray;
        bar.ButtonHoverBackgroundColor = light ? ColorHelper.FromArgb(0x14, 0, 0, 0) : ColorHelper.FromArgb(0x24, 255, 255, 255);
        bar.ButtonHoverForegroundColor = light ? Colors.Black : Colors.White;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        settings.Theme = settings.Theme switch { "system" => "light", "light" => "dark", _ => "system" };
        settings.Save();
        ApplyTheme();
    }

    // ---- explorer context menu ---------------------------------------------------------------------------------

    private void MenuBox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MenuBox.IsChecked == true) ContextMenu.Install(Environment.ProcessPath ?? "");
            else ContextMenu.Uninstall();
            HideHint();
        }
        catch (Exception ex)
        {
            Log.Write("Could not change the Explorer menu", ex);
            MenuBox.IsChecked = ContextMenu.IsInstalled();
            ShowHint("Windows would not let EveryCopy change the right-click menu: " + ex.Message);
        }
    }

    /// <summary>If the app was moved or reinstalled, point an installed menu at the exe that is actually running.</summary>
    private static void RefreshMenuIfStale()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null && ContextMenu.IsInstalled() && !string.Equals(ContextMenu.InstalledExe(), exe, StringComparison.OrdinalIgnoreCase))
                ContextMenu.Install(exe);
        }
        catch (Exception ex) { Log.Write("Could not refresh the Explorer menu", ex); }
    }

    // ---- adding sources ----------------------------------------------------------------------------------------

    private void AddSource(string path)
    {
        try
        {
            path = Path.GetFullPath(path.Trim().Trim('"'));
            if (!File.Exists(path) && !Directory.Exists(path)) { ShowHint($"\"{path}\" does not exist."); return; }
            if (sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            sources.Add(new SourceEntry(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            ShowHint($"\"{path}\" is not a valid path.");
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files != null) foreach (var file in files) AddSource(file.Path);
        }
        catch (Exception ex) { Log.Write("File picker failed", ex); ShowHint("The file picker could not open: " + ex.Message); }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) AddSource(folder.Path);
        }
        catch (Exception ex) { Log.Write("Folder picker failed", ex); ShowHint("The folder picker could not open: " + ex.Message); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) DestBox.Text = folder.Path;
        }
        catch (Exception ex) { Log.Write("Folder picker failed", ex); ShowHint("The folder picker could not open: " + ex.Message); }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to EveryCopy";
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            foreach (var item in await e.DataView.GetStorageItemsAsync()) AddSource(item.Path);
        }
        catch (Exception ex) { Log.Write("Drop failed", ex); ShowHint("Those items could not be read: " + ex.Message); }
        finally { deferral.Complete(); }
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SourceEntry entry) sources.Remove(entry);
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e) => sources.Clear();

    private void UpdateSources()
    {
        bool any = sources.Count > 0;
        SourcesHeader.Visibility = SourceList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SourcesCount.Text = sources.Count == 1 ? "1 item to transfer" : $"{sources.Count} items to transfer";
        UpdateStart();
    }

    private async void TestSpeed_Click(object sender, RoutedEventArgs e)
    {
        string dest = DestBox.Text.Trim().Trim('"');
        if (!Path.IsPathRooted(dest) || !Directory.Exists(dest)) { SpeedResult.Text = "Choose an existing destination folder first."; return; }

        TestSpeedButton.IsEnabled = false;
        SpeedResult.Text = "Testing…";
        try
        {
            double bytesPerSecond = await Task.Run(() => Benchmark.MeasureWrite(dest, 256L * 1024 * 1024, CancellationToken.None));
            string advice = bytesPerSecond < 80 * 1024 * 1024 ? "Slow: copy one file at a time."
                : bytesPerSecond > 500 * 1024 * 1024 ? "Fast: try Auto for many small files." : "Fine for most copies.";
            SpeedResult.Text = $"Writes at about {Format.Bytes((long)bytesPerSecond)}/s. {advice}";
        }
        catch (Exception ex)
        {
            Log.Write("Speed test failed", ex);
            SpeedResult.Text = "The test could not run: " + (ex is UnauthorizedAccessException ? "no permission to write there." : ex.Message);
        }
        finally { TestSpeedButton.IsEnabled = true; }
    }

    private void DestBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStart();

    private void UpdateStart() => StartButton.IsEnabled = sources.Count > 0 && !string.IsNullOrWhiteSpace(DestBox.Text);

    // ---- options -----------------------------------------------------------------------------------------------

    private void CopyMode_Click(object sender, RoutedEventArgs e) => SetMode(TransferMode.Copy);
    private void MoveMode_Click(object sender, RoutedEventArgs e) => SetMode(TransferMode.Move);

    private void SetMode(TransferMode value)
    {
        mode = value;
        CopyModeButton.Style = (Style)Application.Current.Resources[value == TransferMode.Copy ? "ChipButtonOn" : "ChipButton"];
        MoveModeButton.Style = (Style)Application.Current.Resources[value == TransferMode.Move ? "ChipButtonOn" : "ChipButton"];
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        settings.Collision = (CollisionPolicy)Math.Max(0, CollisionBox.SelectedIndex);
        settings.Threads = ThreadChoices[Math.Clamp(ThreadsBox.SelectedIndex, 0, ThreadChoices.Length - 1)];
        settings.Verify = VerifyBox.IsChecked == true;
        settings.Save();
    }

    // ---- starting ----------------------------------------------------------------------------------------------

    private void Start_Click(object sender, RoutedEventArgs e) => _ = StartTransferAsync();

    private async Task StartTransferAsync(bool popup = false)
    {
        string dest = DestBox.Text.Trim().Trim('"');
        if (sources.Count == 0) { ShowHint("Add the files or folders to transfer first."); return; }

        string full;
        try
        {
            if (!Path.IsPathRooted(dest)) { ShowHint("Enter the full path of the destination folder, such as D:\\Backup."); return; }
            full = Path.GetFullPath(dest);
            if (File.Exists(full)) { ShowHint("The destination is a file. Choose a folder."); return; }
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) { ShowHint($"The drive {root} is not available. Connect it, or choose another destination."); return; }
            Directory.CreateDirectory(PathUtil.Long(full));
        }
        catch (UnauthorizedAccessException) { ShowHint("You do not have permission to write to that destination."); return; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            ShowHint("That destination cannot be used: " + ex.Message);
            return;
        }

        foreach (var s in sources)
        {
            if (s.IsFolder && PathUtil.IsInside(full, s.Path)) { ShowHint($"\"{s.Name}\" cannot be copied into itself. Choose a destination outside it."); return; }
        }

        bool windowsCopy = settings.UseWindowsCopy;
        if (settings.AskWhichCopier)
        {
            // The question lives in the app window, so a popup transfer has to open it just for the answer.
            bool revealed = popup && (offscreen || !AppWindow.IsVisible);
            if (revealed) BringToFront();
            bool? answered = await AskCopierAsync();
            if (revealed) AppWindow.Hide();
            if (answered is not { } answer) { sources.Clear(); return; } // cancelled: nothing is started
            windowsCopy = answer;
        }

        var paths = sources.Select(s => s.Path).ToList();
        bool move = mode == TransferMode.Move;
        settings.LastDestination = full;
        settings.Save();
        sources.Clear();
        HideHint();

        if (windowsCopy)
        {
            try
            {
                IntPtr owner = WindowNative.GetWindowHandle(this);
                string? error = await Task.Run(() => NativeShell.Transfer(paths, full, move, owner));
                if (error != null) ShowHint(error);
            }
            catch (Exception ex)
            {
                Log.Write("Windows copy failed", ex);
                ShowHint("Windows copy could not run: " + ex.Message);
            }
            return;
        }

        queue.Enqueue(paths, full, new JobOptions(mode, settings.Collision, settings.Verify, settings.Threads));
        UpdateQueueChrome();
        if (popup) ShowProgress();
    }

    private bool asking;

    /// <summary>Asks which copier to use. Returns true for Windows copy, false for EveryCopy, null if cancelled.</summary>
    private async Task<bool?> AskCopierAsync()
    {
        if (asking) return null;
        asking = true;
        try
        {
            var dontAsk = new CheckBox { Content = "Don't ask every time", IsChecked = true };
            var body = new StackPanel { Spacing = 14, MaxWidth = 420 };
            body.Children.Add(new TextBlock
            {
                Text = "EveryCopy queues transfers, resumes after a disconnect and can verify files. Windows copy uses the regular Explorer copy window.",
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(dontAsk);

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Which copier do you want to use?",
                Content = body,
                PrimaryButtonText = "EveryCopy",
                SecondaryButtonText = "Windows copy",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return null;

            bool windows = result == ContentDialogResult.Secondary;
            if (dontAsk.IsChecked == true)
            {
                settings.AskWhichCopier = false;
                settings.UseWindowsCopy = windows;
                settings.Save();
                AskBox.IsChecked = false;
            }
            return windows;
        }
        finally { asking = false; }
    }

    private void AskBox_Click(object sender, RoutedEventArgs e)
    {
        settings.AskWhichCopier = AskBox.IsChecked == true;
        settings.Save();
    }

    private void ShowHint(string text)
    {
        Hint.Text = text;
        Hint.Visibility = Visibility.Visible;
        if (offscreen || !AppWindow.IsVisible) BringToFront(); // the message is in the app window: show it
    }

    private void HideHint() => Hint.Visibility = Visibility.Collapsed;

    // ---- queue ------------------------------------------------------------------------------------------------

    private void Tick()
    {
        foreach (var job in queue.Jobs.ToList())
        {
            job.TryRefresh();
            if (job.IsFinished && recorded.Add(job))
            {
                HistoryStore.Add(HistoryStore.From(job));
                // With several drives filling at once, each person needs to hear when theirs is done.
                if (queue.Jobs.Count > 1)
                    tray?.Notify($"{job.DestLabel}: {job.StatusText.ToLowerInvariant()}", $"{job.Title} → {job.ToName}");
            }
        }
        progressWindow?.Refresh();
        UpdateQueueChrome();
        UpdateTaskbar();
        PersistQueue();
        CheckAllDone();
    }

    // ---- remembering, reporting ---------------------------------------------------------------------------------

    private string Signature() => string.Join('|', queue.Jobs.Where(j => !j.IsFinished).Select(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode));

    /// <summary>Whenever the set of unfinished transfers changes, write it down so a restart or crash does not lose them.</summary>
    private void PersistQueue()
    {
        string signature = Signature();
        if (signature == savedSignature) return;
        savedSignature = signature;
        JobStore.Save(queue.Jobs);
    }

    private void RestoreQueue()
    {
        var stored = JobStore.Load();
        foreach (var job in stored)
            queue.Enqueue(job.Sources, job.Destination, new JobOptions(job.Mode, job.Collision, job.Verify, job.Threads, Resume: true));
        savedSignature = Signature();
        if (stored.Count > 0)
            tray?.Notify("EveryCopy picked up where it left off", stored.Count == 1 ? "1 unfinished transfer is continuing." : $"{stored.Count} unfinished transfers are continuing.");
        UpdateQueueChrome();
        if (stored.Count > 0 && settings.ProgressPopup && (offscreen || !AppWindow.IsVisible)) ShowProgress();
    }

    private void UpdateTaskbar()
    {
        var job = queue.Jobs.FirstOrDefault(j => j.IsActive);
        if (job == null) taskbar?.Set(TaskbarProgress.State.None, 0);
        else if (job.DeviceWait != null) taskbar?.Set(TaskbarProgress.State.Error, job.Percent);
        else if (job.IsPaused) taskbar?.Set(TaskbarProgress.State.Paused, job.Percent);
        else if (job.IsScanning) taskbar?.Set(TaskbarProgress.State.Indeterminate, 0);
        else taskbar?.Set(TaskbarProgress.State.Normal, job.Percent);
    }

    /// <summary>Tells the user when the queue empties, and sleeps or shuts down if they asked for that and nothing went wrong.</summary>
    private void CheckAllDone()
    {
        bool busy = queue.HasActive;
        bool justFinished = wasBusy && !busy && queue.Jobs.Count > 0;
        wasBusy = busy;
        if (!justFinished) return;

        bool clean = queue.Jobs.All(j => j.State == JobState.Done);
        tray?.Notify(clean ? "All transfers finished" : "Transfers finished with problems",
            clean ? "Everything was copied." : "Some items need attention. Open EveryCopy to see which.");
        if (!clean || settings.WhenDone == "nothing") return;

        try
        {
            if (settings.WhenDone == "shutdown")
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 60 /c \"EveryCopy finished all transfers\"") { CreateNoWindow = true, UseShellExecute = false });
                tray?.Notify("Shutting down in 60 seconds", "Run  shutdown /a  to cancel.");
            }
            else SetSuspendState(false, false, false);
        }
        catch (Exception ex) { Log.Write("Could not " + settings.WhenDone, ex); }
    }

    private void WhenDone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        settings.WhenDone = WhenDoneChoices[Math.Clamp(WhenDoneBox.SelectedIndex, 0, WhenDoneChoices.Length - 1)];
        settings.Save();
    }

    /// <summary>Asked once, so nothing that changes how Windows behaves is switched on behind the user's back.</summary>
    private async Task FirstRunAsync()
    {
        if (settings.FirstRunDone) return;
        await Task.Delay(500);
        if (!AppWindow.IsVisible) return; // started hidden: ask when the window is first opened instead

        var menu = new CheckBox { Content = "Add Copy / Move / Paste with EveryCopy to the right-click menu", IsChecked = true };
        var intercept = new CheckBox { Content = "Use EveryCopy when I press Ctrl+V in Explorer (keeps it in the tray)", IsChecked = true };
        var startup = new CheckBox { Content = "Start EveryCopy with Windows", IsChecked = false };
        var body = new StackPanel { Spacing = 8, MaxWidth = 440 };
        body.Children.Add(new TextBlock { Text = "Choose how EveryCopy fits into Windows. You can change any of this later under Settings.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(menu);
        body.Children.Add(intercept);
        body.Children.Add(startup);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Welcome to EveryCopy",
            Content = body,
            PrimaryButtonText = "Continue",
            DefaultButton = ContentDialogButton.Primary,
        };
        await dialog.ShowAsync();

        settings.FirstRunDone = true;
        settings.InterceptPaste = intercept.IsChecked == true;
        settings.Save();
        InterceptBox.IsChecked = settings.InterceptPaste;
        try
        {
            if (menu.IsChecked == true) ContextMenu.Install(Environment.ProcessPath ?? "");
            Startup.Set(startup.IsChecked == true, Environment.ProcessPath ?? "");
        }
        catch (Exception ex) { Log.Write("First-run setup failed", ex); ShowHint("Some of those choices could not be applied: " + ex.Message); }
        MenuBox.IsChecked = ContextMenu.IsInstalled();
        StartupBox.IsChecked = Startup.IsEnabled();
    }

    private void UpdateQueueChrome()
    {
        int active = queue.Jobs.Count(j => !j.IsFinished);
        EmptyState.Visibility = queue.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFinishedButton.Visibility = queue.Jobs.Any(j => j.IsFinished) ? Visibility.Visible : Visibility.Collapsed;
        QueueCount.Text = queue.Jobs.Count == 0 ? "" : active > 0 ? $"{active} in progress or waiting" : "All done";

        var running = queue.Jobs.FirstOrDefault(j => j.IsActive);
        Title = running != null ? $"{running.Percent:0}% · {Format.Bytes((long)running.Speed)}/s · EveryCopy" : "EveryCopy";
    }

    private static CopyJob? JobOf(object sender) => (sender as FrameworkElement)?.Tag as CopyJob;

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job && sender is FrameworkElement anchor) Tints.ShowEditor(anchor, job, RefreshDrive);
    }

    /// <summary>A drive was renamed or recoloured: every transfer to it (finished ones too) shows the change at once.</summary>
    private void RefreshDrive(string key)
    {
        foreach (var job in queue.Jobs.Where(j => j.DestKey == key)) job.RaiseChanged();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Pause();
    private void Resume_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Resume();
    private void Cancel_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Cancel();

    private void MoveUp_Click(object sender, RoutedEventArgs e) { if (JobOf(sender) is { } job) queue.Move(job, -1); }
    private void MoveDown_Click(object sender, RoutedEventArgs e) { if (JobOf(sender) is { } job) queue.Move(job, +1); }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } job) return;
        queue.Enqueue(job.Sources, job.Destination, job.Options with { Resume = true }); // already-copied files are skipped
        UpdateQueueChrome();
    }

    private void SkipFile_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.SkipCurrent();

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not JobItem item) return;
        queue.Jobs.FirstOrDefault(j => j.Items.Contains(item))?.Remove(item);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } job) return;
        job.Cancel();
        queue.Remove(job);
        UpdateQueueChrome();
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        queue.RemoveFinished();
        UpdateQueueChrome();
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        var entries = HistoryStore.Load();
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 420, MinWidth = 600 };
        var empty = new TextBlock { Text = "Finished transfers will be listed here.", Margin = new Thickness(0, 8, 0, 8) };

        void Fill()
        {
            list.Items.Clear();
            foreach (var entry in entries) list.Items.Add(HistoryRow(entry, () => { entries.Remove(entry); Fill(); }));
            empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Fill();

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(empty);
        body.Children.Add(list);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Transfer history",
            Content = body,
            CloseButtonText = "Close",
            SecondaryButtonText = "Clear history",
            IsSecondaryButtonEnabled = entries.Count > 0,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Secondary) HistoryStore.Clear();
    }

    /// <summary>One line of history: what, from where to where, how it ended, and a button that puts it back in the queue.</summary>
    private UIElement HistoryRow(HistoryStore.Entry entry, Action removed)
    {
        string outcome = entry.State switch
        {
            JobState.Done => "Finished",
            JobState.DoneWithErrors => $"Finished, {entry.Failed} failed",
            JobState.Cancelled => "Cancelled",
            _ => "Failed" + (string.IsNullOrEmpty(entry.Error) ? "" : ": " + entry.Error),
        };
        string speed = entry.Seconds > 0.5 && entry.Bytes > 0 ? $" · {Format.Bytes((long)(entry.Bytes / entry.Seconds))}/s" : "";
        string verb = entry.Mode == TransferMode.Move ? "Moved" : "Copied";

        var grid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = $"{entry.From}  →  {entry.To}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{outcome} · {verb} {entry.Files:N0} file{(entry.Files == 1 ? "" : "s")}, {Format.Bytes(entry.Bytes)} in {Format.Duration(TimeSpan.FromSeconds(entry.Seconds))}{speed}", Style = (Style)Application.Current.Resources["MutedText"], TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = entry.FinishedLocal.ToString("g") + " · " + entry.Destination, Style = (Style)Application.Current.Resources["MutedText"], TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTipService.SetToolTip(text, string.Join("\n", entry.Sources) + "\n→ " + entry.Destination);
        grid.Children.Add(text);

        var again = new Button { Content = "Again", Style = (Style)Application.Current.Resources["GhostButton"], VerticalAlignment = VerticalAlignment.Center };
        again.Click += (_, _) =>
        {
            queue.Enqueue(entry.Sources, entry.Destination, new JobOptions(entry.Mode, entry.Collision, entry.Verify, entry.Threads, Resume: true));
            again.IsEnabled = false;
            again.Content = "Queued";
            UpdateQueueChrome();
        };
        Grid.SetColumn(again, 1);
        grid.Children.Add(again);
        return grid;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } job) return;
        try
        {
            if (!Directory.Exists(job.Destination)) { ShowHint("The destination folder is not available right now."); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{job.Destination}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Could not open the destination", ex); ShowHint("Explorer could not be opened: " + ex.Message); }
    }

    private async void Errors_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } job) return;
        var text = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 560,
            MinHeight = 260,
            MaxHeight = 420,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Text = string.Join("\n\n", job.Failures.Select(f => f.Path + "\n    " + f.Message)),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = job.FailedFiles == 1 ? "1 item could not be transferred" : $"{job.FailedFiles} items could not be transferred",
            Content = text,
            CloseButtonText = "Close",
            PrimaryButtonText = "Copy list",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(text.Text);
            Clipboard.SetContent(package);
        }
    }

    // ---- closing ----------------------------------------------------------------------------------------------

    /// <summary>The window's X only hides it; EveryCopy stays in the tray so transfers and the Ctrl+V takeover keep going.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeConfirmed) return;
        args.Cancel = true;
        AppWindow.Hide();
        if (queue.HasActive) tray?.Notify("EveryCopy is still copying", "It keeps working in the background. Use the tray icon to open or exit.");
    }

    /// <summary>Exit from the tray or the window: asks first if transfers are still running.</summary>
    private async Task RequestExitAsync()
    {
        try
        {
            if (queue.HasActive)
            {
                BringToFront();
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = "Stop the transfers?",
                    Content = "Transfers are still in progress. Exiting cancels them and removes half-copied files.",
                    PrimaryButtonText = "Cancel transfers and exit",
                    CloseButtonText = "Keep copying",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                queue.CancelAll();
                await Task.Run(() => SpinWait.SpinUntil(() => !queue.HasActive, 4000)); // let partial files be cleaned up
                JobStore.Save(queue.Jobs); // cancelled on purpose: nothing to resume next time
            }
        }
        catch (Exception ex) { Log.Write("Exit confirmation failed", ex); }

        closeConfirmed = true;
        pasteHook?.Dispose();
        menuPasteHook?.Dispose();
        tray?.Dispose();
        Close();
        Environment.Exit(0);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _ = RequestExitAsync();

    private void SetIntercept(bool on)
    {
        settings.InterceptPaste = on;
        settings.Save();
        DispatcherQueue.TryEnqueue(() => InterceptBox.IsChecked = on);
    }

    private void DefaultBox_Click(object sender, RoutedEventArgs e)
    {
        bool on = DefaultBox.IsChecked == true;
        settings.UseAsDefault = on;
        if (on)
        {
            settings.InterceptPaste = true;
            settings.AskWhichCopier = false;
            settings.UseWindowsCopy = false;
        }
        settings.Save();
        InterceptBox.IsChecked = settings.InterceptPaste;
        AskBox.IsChecked = settings.AskWhichCopier;
        if (!on) return;
        try
        {
            ContextMenu.Install(Environment.ProcessPath ?? "");
            Startup.Set(true, Environment.ProcessPath ?? "");
            HideHint();
        }
        catch (Exception ex)
        {
            Log.Write("Default setup failed", ex);
            ShowHint("EveryCopy is the default, but some Windows integration could not be set up: " + ex.Message);
        }
        MenuBox.IsChecked = ContextMenu.IsInstalled();
        StartupBox.IsChecked = Startup.IsEnabled();
    }

    private void InterceptBox_Click(object sender, RoutedEventArgs e) => SetIntercept(InterceptBox.IsChecked == true);

    private void PopupBox_Click(object sender, RoutedEventArgs e)
    {
        settings.ProgressPopup = PopupBox.IsChecked == true;
        settings.Save();
    }

    private void StartupBox_Click(object sender, RoutedEventArgs e)
    {
        try { Startup.Set(StartupBox.IsChecked == true, Environment.ProcessPath ?? ""); HideHint(); }
        catch (Exception ex)
        {
            Log.Write("Could not change the startup entry", ex);
            StartupBox.IsChecked = Startup.IsEnabled();
            ShowHint("Windows would not let EveryCopy change its startup entry: " + ex.Message);
        }
    }
}
