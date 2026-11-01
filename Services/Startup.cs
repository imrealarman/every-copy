using Microsoft.Win32;

namespace EveryCopy.Services;

/// <summary>Starts EveryCopy hidden in the tray when the user signs in (HKCU Run key, no admin rights).</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EveryCopy";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --background");
        else key.DeleteValue(ValueName, false);
    }
}
