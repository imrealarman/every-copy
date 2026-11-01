using System.Text.Json;
using System.Text.Json.Serialization;
using EveryCopy.Models;

namespace EveryCopy.Services;

/// <summary>User choices, kept as JSON in %AppData%\EveryCopy.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    public CollisionPolicy Collision { get; set; } = CollisionPolicy.KeepBoth;
    public bool Verify { get; set; }
    /// <summary>Small files copied at once; 1 copies strictly one file after another, 0 lets EveryCopy choose.</summary>
    public int Threads { get; set; } = 1;
    /// <summary>Ask, before each transfer, whether to use EveryCopy or Windows' own copy. Asked on first use.</summary>
    public bool AskWhichCopier { get; set; } = true;
    /// <summary>The remembered answer when not asking: false = EveryCopy (the default), true = Windows copy.</summary>
    public bool UseWindowsCopy { get; set; }
    /// <summary>Ctrl+V in Explorer goes through EveryCopy (needs EveryCopy running in the tray).</summary>
    public bool InterceptPaste { get; set; }
    /// <summary>EveryCopy is the default copier: Ctrl+V and the right-click Paste in Explorer both go through it, without asking.</summary>
    public bool UseAsDefault { get; set; }
    /// <summary>False until the first-run questions (Ctrl+V takeover, right-click menu, start with Windows) have been answered.</summary>
    public bool FirstRunDone { get; set; }
    /// <summary>What to do when every transfer has finished without errors: "nothing", "sleep" or "shutdown".</summary>
    public string WhenDone { get; set; } = "nothing";
    public string LastDestination { get; set; } = "";
    /// <summary>Transfers started from Explorer (Ctrl+V, right-click) show a small progress window instead of opening the app.</summary>
    public bool ProgressPopup { get; set; } = true;
    /// <summary>The progress window opens in its detailed layout (graph, files, items) rather than the compact one.</summary>
    public bool ProgressDetailed { get; set; }

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryCopy", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? FilePath), Json);
            if (loaded != null)
            {
                loaded.Threads = Math.Clamp(loaded.Threads, 0, 32);
                return loaded;
            }
        }
        catch { /* first run, or a damaged file: start from the defaults */ }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { /* losing a preference is not worth interrupting a copy */ }
    }
}
