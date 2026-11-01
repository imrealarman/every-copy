namespace EveryCopy.Services;

/// <summary>
/// What a launch asks for. Plain arguments are source paths (what Explorer's menus and "Send to" pass);
/// <c>--to &lt;folder&gt;</c> sets the destination, <c>--move</c> moves instead of copying, <c>--start</c> begins at once,
/// and <c>--paste &lt;folder&gt;</c> transfers whatever files are on the clipboard into that folder.
/// <c>--install-menu</c> / <c>--uninstall-menu</c> manage the Explorer context menu and exit.
/// </summary>
public sealed record LaunchRequest(IReadOnlyList<string> Paths, string? Destination, bool Move, bool Start, bool Paste = false)
{
    public static LaunchRequest Parse(IEnumerable<string> args)
    {
        var paths = new List<string>();
        string? destination = null;
        bool move = false, start = false, paste = false;

        using var it = args.GetEnumerator();
        while (it.MoveNext())
        {
            string arg = it.Current;
            if (arg.Equals("--to", StringComparison.OrdinalIgnoreCase)) { if (it.MoveNext()) destination = it.Current; }
            else if (arg.Equals("--paste", StringComparison.OrdinalIgnoreCase))
            {
                paste = true;
                if (it.MoveNext()) destination = it.Current;
            }
            else if (arg.Equals("--move", StringComparison.OrdinalIgnoreCase)) move = true;
            else if (arg.Equals("--start", StringComparison.OrdinalIgnoreCase)) start = true;
            else if (arg.StartsWith("--", StringComparison.Ordinal)) { /* a flag for somebody else, such as --background */ }
            else if (arg.Length > 0) paths.Add(arg);
        }
        return new LaunchRequest(paths, destination, move, start, paste);
    }
}
