namespace EveryCopy.Services;

/// <summary>Path helpers: long-path prefixing, unique names and containment checks.</summary>
public static class PathUtil
{
    /// <summary>Adds the \\?\ prefix to paths too long for the classic 260 limit; shorter paths are returned untouched.</summary>
    public static string Long(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    /// <summary>"name.ext" -> "name (2).ext", "name (3).ext"... the first one nothing exists at.</summary>
    public static string UniqueName(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Exists(candidate)) return candidate;
        }
    }

    public static bool Exists(string path) => File.Exists(Long(path)) || Directory.Exists(Long(path));

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':' && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or somewhere beneath it.</summary>
    public static bool IsInside(string child, string parent)
    {
        string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + Path.DirectorySeparatorChar;
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
