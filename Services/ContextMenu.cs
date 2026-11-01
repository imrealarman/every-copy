using Microsoft.Win32;

namespace EveryCopy.Services;

/// <summary>
/// Adds "Copy with EveryCopy", "Move with EveryCopy" (files, folders, drives) and "Paste with EveryCopy" (folder background)
/// to Explorer's right-click menu. Everything lives under HKCU\Software\Classes, so it needs no admin rights. On Windows 11
/// the entries appear under "Show more options".
/// </summary>
public static class ContextMenu
{
    private const string DefaultRoot = @"Software\Classes";

    private sealed record Entry(string Key, string Label, string Arguments);

    private static readonly Entry[] Entries =
    {
        new(@"*\shell\EveryCopy.Copy", "Copy with EveryCopy", "\"%1\""),
        new(@"*\shell\EveryCopy.Move", "Move with EveryCopy", "--move \"%1\""),
        new(@"Directory\shell\EveryCopy.Copy", "Copy with EveryCopy", "\"%1\""),
        new(@"Directory\shell\EveryCopy.Move", "Move with EveryCopy", "--move \"%1\""),
        new(@"Drive\shell\EveryCopy.Copy", "Copy with EveryCopy", "\"%1\""),
        new(@"Directory\Background\shell\EveryCopy.Paste", "Paste with EveryCopy", "--paste \"%V\""),
    };

    /// <param name="exePath">The EveryCopy.exe the menu should launch.</param>
    /// <param name="root">Registry path the verbs go under; only tests change it.</param>
    public static void Install(string exePath, string root = DefaultRoot)
    {
        foreach (var entry in Entries)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{entry.Key}", true);
            key.SetValue("", entry.Label);
            key.SetValue("Icon", $"\"{exePath}\",0");
            using var command = key.CreateSubKey("command", true);
            command.SetValue("", $"\"{exePath}\" {entry.Arguments}");
        }
    }

    public static void Uninstall(string root = DefaultRoot)
    {
        foreach (var entry in Entries)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{entry.Key}", false); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log.Write($"Could not remove {entry.Key}", ex); }
        }
    }

    public static bool IsInstalled(string root = DefaultRoot)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{root}\{Entries[0].Key}\command");
        return key?.GetValue("") is string;
    }

    /// <summary>The exe the installed menu points at, so the app can notice after it was moved or reinstalled.</summary>
    public static string? InstalledExe(string root = DefaultRoot)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{root}\{Entries[0].Key}\command");
        string? command = key?.GetValue("") as string;
        if (command == null || !command.StartsWith('"')) return null;
        int end = command.IndexOf('"', 1);
        return end > 1 ? command[1..end] : null;
    }
}
