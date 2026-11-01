using System.Runtime.InteropServices;
using System.Text;

namespace EveryCopy.Services;

/// <summary>
/// Reads the files Explorer put on the clipboard straight through Win32, so it works while EveryCopy's window is hidden
/// (the WinRT clipboard wants a foreground window).
/// </summary>
public static class ClipboardFiles
{
    private const uint CfHDrop = 15;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);

    public static bool HasFiles => IsClipboardFormatAvailable(CfHDrop);

    /// <summary>The file paths on the clipboard and whether they were cut (to be moved). Empty if there are none.</summary>
    public static (List<string> Paths, bool Cut) Read()
    {
        var paths = new List<string>();
        bool cut = false;

        // Another program may hold the clipboard for a moment, so try a few times.
        bool opened = false;
        for (int attempt = 0; attempt < 10 && !(opened = OpenClipboard(IntPtr.Zero)); attempt++) Thread.Sleep(30);
        if (!opened) throw new IOException("The clipboard is in use by another program. Try again.");

        try
        {
            IntPtr drop = GetClipboardData(CfHDrop);
            if (drop != IntPtr.Zero)
            {
                uint count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var name = new StringBuilder((int)DragQueryFile(drop, i, null, 0) + 1);
                    DragQueryFile(drop, i, name, (uint)name.Capacity);
                    if (name.Length > 0) paths.Add(name.ToString());
                }
            }

            IntPtr effect = GetClipboardData(RegisterClipboardFormat("Preferred DropEffect"));
            if (effect != IntPtr.Zero)
            {
                IntPtr locked = GlobalLock(effect);
                if (locked != IntPtr.Zero)
                {
                    try { cut = (Marshal.ReadInt32(locked) & 2) != 0; } // Explorer's "Cut"
                    finally { GlobalUnlock(effect); }
                }
            }
        }
        finally { CloseClipboard(); }
        return (paths, cut);
    }
}
