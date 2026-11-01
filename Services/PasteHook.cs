using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EveryCopy.Services;

/// <summary>
/// Makes Ctrl+V in an Explorer window (or on the desktop) hand the copied files to EveryCopy instead of Windows.
/// A low-level keyboard hook sees the key first; it only claims it when Explorer's file list has focus, files are on the
/// clipboard and the folder being viewed can be read, so typing in the address bar, search box or a rename box is untouched.
/// </summary>
public sealed class PasteHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x100, WmSysKeyDown = 0x104;
    private const int VkControl = 0x11, VkShift = 0x10, VkMenu = 0x12, VkV = 0x56;
    private const uint LlkhfInjected = 0x10;
    private const uint CfHDrop = 15;
    private static readonly UIntPtr OwnMarker = (UIntPtr)0x45434F50; // stamped on the Ctrl+V we send back to Explorer

    /// <summary>Test seam: scripted key presses are "injected", which the hook normally ignores.</summary>
    private static readonly bool AllowInjected = Environment.GetEnvironmentVariable("EVERYCOPY_TEST_INJECTED") == "1";

    /// <summary>The window class Windows 11's newer Explorer reports for focus, for its file list and for its text boxes alike.</summary>
    private const string Bridge = "Microsoft.UI.Content.DesktopChildSiteBridge";

    private static readonly string[] FileListClasses = { "DirectUIHWND", "SysListView32", "SHELLDLL_DefView", "DUIViewWndClassName", Bridge };

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public uint VkCode, ScanCode, Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KeybdInput Ki; [FieldOffset(0)] public MouseInput Mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion U; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    // Just enough of UI Automation to ask what kind of control has focus (vtable order matters; the placeholders keep it).
    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IntPtr a, IntPtr b);
        [PreserveSig] int CompareRuntimeIds(IntPtr a, IntPtr b);
        [PreserveSig] int GetRootElement(out IntPtr element);
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IntPtr element);
        [PreserveSig] int ElementFromPoint(long point, out IntPtr element);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr id);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IntPtr element);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr elements);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr element);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr elements);
        [PreserveSig] int BuildUpdatedCache(IntPtr cache, out IntPtr element);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc proc;
    private readonly Func<bool> enabled;
    private readonly Action<IntPtr, bool> onPaste;
    private IntPtr hook;

    /// <param name="enabled">Asked on every Ctrl+V, so switching the feature off takes effect at once.</param>
    /// <param name="onPaste">
    /// Called from inside the hook with the Explorer window (and whether it is the desktop). Keep it quick and do the real work
    /// afterwards: COM calls such as <see cref="ResolveFolder"/> are not allowed while a hook is running.
    /// </param>
    public PasteHook(Func<bool> enabled, Action<IntPtr, bool> onPaste)
    {
        this.enabled = enabled;
        this.onPaste = onPaste;
        proc = Callback; // kept in a field so the GC cannot collect the delegate while Windows holds it
        hook = SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Write("Could not install the Ctrl+V hook, error " + Marshal.GetLastWin32Error());
    }

    public bool IsActive => hook != IntPtr.Zero;

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && ((int)wParam is WmKeyDown or WmSysKeyDown))
            {
                var key = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (key.VkCode == VkV && (AllowInjected || (key.Flags & LlkhfInjected) == 0) && Down(VkControl) && !Down(VkShift) && !Down(VkMenu)
                    && key.Extra != OwnMarker && enabled() && TryClaim(out IntPtr window, out bool desktop))
                {
                    onPaste(window, desktop);
                    return (IntPtr)1; // swallowed: Explorer never sees this Ctrl+V
                }
            }
        }
        catch (Exception ex) { Log.Write("Ctrl+V hook error", ex); }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static void Trace(string text) { if (AllowInjected) Log.Write("hook: " + text); }

    /// <summary>True when Explorer's file list is focused with files on the clipboard: this Ctrl+V is ours to take.</summary>
    private static bool TryClaim(out IntPtr window, out bool desktop)
    {
        window = GetForegroundWindow();
        desktop = false;
        if (window == IntPtr.Zero) return false;

        string windowClass = ClassOf(window);
        Trace($"Ctrl+V, foreground class {windowClass}");
        desktop = windowClass is "Progman" or "WorkerW";
        if (windowClass != "CabinetWClass" && !desktop) return false;
        if (!IsClipboardFormatAvailable(CfHDrop)) return false;

        // Only the file list: never a rename box, the address bar or the search box.
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero) return false;
        Trace($"focus class {ClassOf(info.Focus)}");
        return FileListClasses.Contains(ClassOf(info.Focus));
    }

    /// <summary>
    /// The folder an Explorer window (or the desktop) is showing, or null when pasting there is not ours to do: virtual
    /// folders such as This PC or search results, or several tabs sharing one window.
    /// </summary>
    public static string? ResolveFolder(IntPtr window, bool desktop)
    {
        string? path = desktop ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : ExplorerFolder(window);
        Trace($"folder {path}");
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
        if (!desktop && !TitleMatches(window, path)) return null; // a different tab than the one the window list reported
        return path;
    }

    /// <summary>
    /// Explorer's title bar names the folder of the tab on screen. If it does not match the folder we resolved (several tabs
    /// share one window and the list can report another one), we would paste into the wrong place, so we say no.
    /// </summary>
    private static bool TitleMatches(IntPtr window, string path)
    {
        var text = new StringBuilder(512);
        if (GetWindowText(window, text, text.Capacity) <= 0) return true; // no title to compare with: nothing contradicts it
        string title = text.ToString();
        string trimmed = path.TrimEnd('\\');
        string leaf = Path.GetFileName(trimmed);
        bool ok = leaf.Length == 0
            ? title.Contains(trimmed[..Math.Min(2, trimmed.Length)], StringComparison.OrdinalIgnoreCase)  // a drive root such as C:\
            : title.StartsWith(leaf, StringComparison.OrdinalIgnoreCase) || title.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        Trace($"title '{title}' vs folder '{leaf}' -> {ok}");
        return ok;
    }

    /// <summary>
    /// True when a text box (rename box, address bar, search box) has focus in <paramref name="window"/>, so Ctrl+V should
    /// paste text there. If that cannot be determined, answers true only where the window class alone could not tell.
    /// </summary>
    public static bool TextBoxFocused(IntPtr window)
    {
        const int ControlTypePropertyId = 30003, Edit = 50004, ComboBox = 50003, Document = 50030;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        bool ambiguous = GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) && ClassOf(info.Focus) == Bridge;
        try
        {
            var automation = (IUIAutomation)new CUIAutomation();
            if (automation.GetFocusedElement(out var element) != 0 || element == null) return ambiguous;
            if (element.GetCurrentPropertyValue(ControlTypePropertyId, out object value) != 0) return ambiguous;
            Trace($"focused control type {value}");
            return value is int type && type is Edit or ComboBox or Document;
        }
        catch (Exception ex)
        {
            Trace("UI Automation failed: " + ex.Message);
            return ambiguous;
        }
    }

    /// <summary>Sends the Ctrl+V back to Explorer so it pastes the normal way. The hook recognises and ignores it.</summary>
    public static void PassThrough()
    {
        Input Key(ushort vk, bool up) => new()
        {
            Type = 1,
            U = new InputUnion { Ki = new KeybdInput { Vk = vk, Flags = up ? 2u : 0u, Extra = OwnMarker } },
        };
        var keys = new[] { Key(VkControl, false), Key(VkV, false), Key(VkV, true), Key(VkControl, true) };
        SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
    }

    /// <summary>The folder shown in an Explorer window, or null if it is unclear (for example several tabs share the window).</summary>
    private static string? ExplorerFolder(IntPtr hwnd)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    if ((long)window.HWND != hwnd.ToInt64()) continue;
                    string? p = window.Document?.Folder?.Self?.Path as string;
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
                catch { /* a window that closed while we looked */ }
            }
            if (paths.Count <= 1) return paths.FirstOrDefault();
            // Several tabs share this window: the title bar names the tab on screen. One clear match is that tab; otherwise give up.
            var onScreen = paths.Where(p => TitleMatches(hwnd, p)).ToList();
            Trace($"{paths.Count} tabs, {onScreen.Count} match the title");
            return onScreen.Count == 1 ? onScreen[0] : null;
        }
        catch (Exception ex)
        {
            Log.Write("Could not read the Explorer folder", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }
}
