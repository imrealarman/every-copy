using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EveryCopy.Services;

/// <summary>
/// Gives every destination drive its own colour (and an optional name such as "Sara's USB"), remembered between runs,
/// so that when several drives are being filled at once each person can pick out their own transfer at a glance.
/// A drive is recognised by its volume serial number, so it keeps its colour and name if Windows gives it another drive letter.
/// </summary>
public static class Destinations
{
    /// <summary>
    /// Colours that are easy to tell apart and never read as a status: no green, red, yellow or orange, which the app keeps
    /// for "done", "error" and "waiting". Listed in the order they are handed out.
    /// </summary>
    public sealed record Swatch(string Name, byte R, byte G, byte B);

    public static readonly Swatch[] Palette =
    {
        new("Blue", 0x3B, 0x82, 0xF6),
        new("Pink", 0xEC, 0x48, 0x99),
        new("Cyan", 0x22, 0xD3, 0xEE),
        new("Purple", 0x8B, 0x5C, 0xF6),
        new("Grey", 0x94, 0xA3, 0xB8),
        new("Magenta", 0xD9, 0x46, 0xEF),
        new("Indigo", 0x4F, 0x46, 0xE5),
    };

    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public int Color { get; set; }
    }

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? known;

    /// <summary>Where the names and colours are kept; tests point this somewhere else.</summary>
    public static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryCopy", "destinations.json");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder? fs, int fsSize);

    /// <summary>The drive (or network share) a path lives on, such as <c>E:\</c>; transfers to the same one wait for each other.</summary>
    public static string RootOf(string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        return (root.Length == 0 ? path : root).ToUpperInvariant();
    }

    /// <summary>A stable identity for the drive: its volume serial number, or the root itself when that cannot be read.</summary>
    public static string KeyOf(string path)
    {
        string root = RootOf(path);
        try
        {
            // Test seam: drives made with "subst" share their real volume's serial number, so tell them apart by letter.
            if (Environment.GetEnvironmentVariable("EVERYCOPY_TEST_KEY_BY_ROOT") == "1") return "root:" + root;
            if (root.Length >= 2 && root[1] == ':' && GetVolumeInformationW(root, null, 0, out uint serial, out _, out _, null, 0))
                return "vol:" + serial.ToString("X8");
        }
        catch { /* fall back to the root below */ }
        return "root:" + root;
    }

    private static Dictionary<string, Entry> Known()
    {
        if (known != null) return known;
        try
        {
            if (File.Exists(FilePath))
                known = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Log.Write("Could not read the destination names", ex); }
        return known ??= new Dictionary<string, Entry>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Known(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Could not save the destination names", ex); }
    }

    /// <summary>Forgets what was loaded (tests use this together with <see cref="FilePath"/>).</summary>
    public static void Reset() { lock (Gate) known = null; }

    /// <summary>
    /// Makes sure <paramref name="key"/> has a colour that no other drive in <paramref name="inUse"/> (the drives being
    /// copied to right now) has. The drive keeps its old colour whenever that is free, so it looks the same each time.
    /// </summary>
    public static int Assign(string key, IEnumerable<string> inUse)
    {
        lock (Gate)
        {
            var all = Known();
            var others = inUse.Where(k => k != key).Distinct().ToList();
            var taken = others.Select(k => all.TryGetValue(k, out var e) ? e.Color : -1).ToHashSet();

            if (all.TryGetValue(key, out var mine) && !taken.Contains(mine.Color)) return mine.Color;

            int pick = Enumerable.Range(0, Palette.Length).FirstOrDefault(i => !taken.Contains(i), -1);
            if (pick < 0) pick = others.Count % Palette.Length; // more drives than colours: share, but spread evenly
            if (mine == null) all[key] = mine = new Entry();
            mine.Color = pick;
            Save();
            return pick;
        }
    }

    public static int ColorOf(string key)
    {
        lock (Gate) return Known().TryGetValue(key, out var e) ? Math.Clamp(e.Color, 0, Palette.Length - 1) : 0;
    }

    public static string NameOf(string key)
    {
        lock (Gate) return Known().TryGetValue(key, out var e) ? e.Name : "";
    }

    /// <summary>Gives the drive a name of the user's choosing; an empty name removes it.</summary>
    public static void SetName(string key, string? name)
    {
        lock (Gate)
        {
            var all = Known();
            if (!all.TryGetValue(key, out var e)) all[key] = e = new Entry();
            e.Name = (name ?? "").Trim();
            if (e.Name.Length > 40) e.Name = e.Name[..40];
            Save();
        }
    }

    public static void SetColor(string key, int color)
    {
        lock (Gate)
        {
            var all = Known();
            if (!all.TryGetValue(key, out var e)) all[key] = e = new Entry();
            e.Color = Math.Clamp(color, 0, Palette.Length - 1);
            Save();
        }
    }

    /// <summary>How people refer to it: the name if there is one, together with the colour name; otherwise just the colour name.</summary>
    public static string Label(string key)
    {
        string colour = Palette[ColorOf(key)].Name;
        string name = NameOf(key);
        return name.Length == 0 ? colour : $"{name} · {colour}";
    }
}
