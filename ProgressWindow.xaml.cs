using System.Diagnostics;
using System.Runtime.InteropServices;
using EveryCopy.Models;
using EveryCopy.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace EveryCopy;

/// <summary>
/// The small window that shows transfers when they were started from Explorer (Ctrl+V, right-click), so the whole app does not
/// have to open. One card per transfer, each in the colour of the drive it fills, so several people copying to their own USB
/// sticks at the same time can each follow theirs. Compact: names, bar, speed. Detailed: paths, graph, files, removable items.
/// It closes itself when everything finished cleanly.
/// </summary>
public sealed partial class ProgressWindow : Window
{
    private const int EdgeGap = 16;
    private const int PopupWidth = 470;
    private const int CompactCard = 150;   // a compact card plus its gap
    private const int FooterAndPadding = 74;
    private const int MaxCompactCards = 4;
    private const int DetailedHeight = 640;

    private readonly AppSettings settings;
    private readonly CopyQueue queue;
    private readonly Action openApp;
    private bool detailed;
    private int shownHeight = -1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? closeTimer;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    public ProgressWindow(AppSettings settings, CopyQueue queue, Action openApp)
    {
        this.settings = settings;
        this.queue = queue;
        this.openApp = openApp;
        InitializeComponent();
        Title = "EveryCopy";

        Root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        try
        {
            string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch (Exception ex) { Log.Write("Could not load the progress window icon", ex); }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }

        JobsList.ItemsSource = queue.Jobs;
        detailed = settings.ProgressDetailed;
        Refresh();
    }

    // ---- layout ------------------------------------------------------------------------------------------------

    /// <summary>Sizes the window to its cards and anchors it to the bottom-right corner, where Windows puts its own transfer windows.</summary>
    private void ApplyLayout()
    {
        ModeText.Text = detailed ? "Compact" : "Details";
        ModeIcon.Glyph = detailed ? "" : "";

        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int cards = Math.Max(1, queue.Jobs.Count);
        int height = detailed ? DetailedHeight : Math.Min(cards, MaxCompactCards) * CompactCard + FooterAndPadding;
        height = Math.Min((int)(height * scale), (int)(work.Height * 0.9));
        if (height == shownHeight) return;
        shownHeight = height;

        int w = Math.Min((int)(PopupWidth * scale), work.Width);
        AppWindow.ResizeClient(new SizeInt32(w, height));
        var outer = AppWindow.Size;
        int gap = (int)(EdgeGap * scale);
        AppWindow.Move(new PointInt32(work.X + work.Width - outer.Width - gap, work.Y + work.Height - outer.Height - gap));
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        detailed = !detailed;
        settings.ProgressDetailed = detailed;
        settings.Save();
        Refresh();
    }

    // ---- showing the transfers ---------------------------------------------------------------------------------

    /// <summary>Called by the main window's timer (and once when created): size the window and tell the cards which layout to use.</summary>
    public void Refresh()
    {
        var jobs = queue.Jobs.ToList();
        if (jobs.Count == 0) { Close(); return; } // everything was removed from the list

        foreach (var job in jobs)
        {
            if (job.ShowDetails == detailed) continue;
            job.ShowDetails = detailed;
            job.RaiseChanged();
        }
        ApplyLayout();

        int running = jobs.Count(j => j.IsActive);
        int waiting = jobs.Count(j => j.State == JobState.Queued);
        SummaryText.Text = jobs.Count == 1 ? "" : $"{running} copying · {waiting} waiting · {jobs.Count(j => j.IsFinished)} done";

        var first = jobs.FirstOrDefault(j => j.IsActive);
        Title = first == null ? "EveryCopy"
            : jobs.Count(j => j.IsActive) > 1 ? $"{running} transfers · EveryCopy"
            : $"{first.Percent:0}% · {Format.Bytes((long)first.Speed)}/s · EveryCopy";

        CloseWhenDone(jobs.All(j => j.State == JobState.Done));
    }

    /// <summary>A clean finish is shown for a moment, then the window goes away; a problem keeps it open for the user to read.</summary>
    private void CloseWhenDone(bool allClean)
    {
        if (!allClean) { closeTimer?.Stop(); closeTimer = null; return; }
        if (closeTimer != null) return;

        closeTimer = DispatcherQueue.CreateTimer();
        closeTimer.Interval = TimeSpan.FromSeconds(3);
        closeTimer.IsRepeating = false;
        closeTimer.Tick += (_, _) => { closeTimer = null; try { Close(); } catch (Exception ex) { Log.Write("Could not close the progress window", ex); } };
        closeTimer.Start();
    }

    // ---- buttons -----------------------------------------------------------------------------------------------

    private static CopyJob? JobOf(object sender) => (sender as FrameworkElement)?.Tag as CopyJob;

    private void Pause_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Pause();
    private void Resume_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Resume();
    private void Cancel_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Cancel();
    private void SkipFile_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.SkipCurrent();

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not JobItem item) return;
        queue.Jobs.FirstOrDefault(j => j.Items.Contains(item))?.Remove(item);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job && sender is FrameworkElement anchor)
            Tints.ShowEditor(anchor, job, key => { foreach (var j in queue.Jobs.Where(j => j.DestKey == key)) j.RaiseChanged(); });
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e) => openApp();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } job) return;
        try
        {
            if (Directory.Exists(job.Destination))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{job.Destination}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Could not open the destination", ex); }
    }
}
