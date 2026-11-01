using EveryCopy.Services;
using Microsoft.UI.Xaml;

namespace EveryCopy;

public partial class App : Application
{
    private MainWindow? window;
    private readonly string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();

    public App()
    {
        // "--install-menu" / "--uninstall-menu" are what the installer runs: do the registry work and leave.
        if (HandleMenuFlags()) Environment.Exit(0);

        if (!SingleInstance.Acquire(args)) Environment.Exit(0); // already running: it was handed our arguments

        // Nothing should take the app down in the middle of a copy: log it and carry on.
        UnhandledException += (_, e) => { Log.Write("Unhandled UI exception", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Unobserved task exception", e.Exception); e.SetObserved(); };

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs launchArgs)
    {
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var request = LaunchRequest.Parse(args);
        // A paste or an Explorer "start" may only need the small progress window, so keep the main window out of sight at first.
        bool quiet = request.Paste || (request.Start && request.Paths.Count > 0 && !string.IsNullOrWhiteSpace(request.Destination));
        window = new MainWindow(request, background || quiet);
        window.Activate(); // the window has to open once for its content to load; a background start hides it again
        SingleInstance.Listen(later => window.DispatcherQueue.TryEnqueue(() =>
        {
            try { window.Handle(LaunchRequest.Parse(later)); }
            catch (Exception ex) { Log.Write("Could not handle a launch request", ex); }
        }));
    }

    private bool HandleMenuFlags()
    {
        string exe = Environment.ProcessPath ?? "";
        try
        {
            if (args.Contains("--install-menu", StringComparer.OrdinalIgnoreCase)) { ContextMenu.Install(exe); return true; }
            if (args.Contains("--uninstall-menu", StringComparer.OrdinalIgnoreCase)) { ContextMenu.Uninstall(); return true; }
        }
        catch (Exception ex) { Log.Write("Explorer menu change failed", ex); return true; }
        return false;
    }
}
