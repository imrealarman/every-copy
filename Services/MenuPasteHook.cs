using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EveryCopy.Services;

/// <summary>
/// Makes the "Paste" item of Explorer's right-click menu (on empty space in a folder or on the desktop) hand the copied files to
/// EveryCopy. Explorer's menu cannot be extended to replace its own Paste, so this watches for it: after a right-click in
/// Explorer a worker thread looks for the open menu with UI Automation and remembers where its "Paste" item is; a low-level
/// mouse hook then swallows a click on that spot (and the P accelerator) and closes the menu. Nothing is done inside the hook
/// except comparing numbers, so Explorer is never held up, and any doubt means the click goes to Explorer untouched.
/// Menus are recognised by their English item names ("Paste", "Refresh"); other languages fall back to Windows' own paste.
/// </summary>
public sealed class MenuPasteHook : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmLButtonDown = 0x201, WmLButtonUp = 0x202, WmRButtonUp = 0x205;
    private const int VkEscape = 0x1B, VkP = 0x50, VkControl = 0x11, VkMenu = 0x12;
    private const uint CfHDrop = 15, LlmhfInjected = 1, LlkhfInjected = 0x10;
    private static readonly UIntPtr OwnMarker = (UIntPtr)0x45434F51;
    private static readonly string[] PasteNames = { "Paste" };
    private static readonly string[] BackgroundMarkers = { "Refresh" }; // only the empty-space menu has it: item menus paste into a folder we cannot name

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct MsLlHookStruct { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VkCode, ScanCode, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeybdInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeybdInput Ki; [FieldOffset(0)] public MouseInput Mi; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    // UI Automation, only the slots that are used (the vtable order must be kept, hence the placeholders).
    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IntPtr a, IntPtr b);
        [PreserveSig] int CompareRuntimeIds(IntPtr a, IntPtr b);
        [PreserveSig] int GetRootElement(out IntPtr element);
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPoint(long point, out IntPtr element);
        [PreserveSig] int GetFocusedElement(out IntPtr element);
        [PreserveSig] int GetRootElementBuildCache(IntPtr c, out IntPtr e);
        [PreserveSig] int ElementFromHandleBuildCache(IntPtr h, IntPtr c, out IntPtr e);
        [PreserveSig] int ElementFromPointBuildCache(long p, IntPtr c, out IntPtr e);
        [PreserveSig] int GetFocusedElementBuildCache(IntPtr c, out IntPtr e);
        [PreserveSig] int CreateTreeWalker(IntPtr c, out IntPtr w);
        [PreserveSig] int GetControlViewWalker(out IntPtr w);
        [PreserveSig] int GetContentViewWalker(out IntPtr w);
        [PreserveSig] int GetRawViewWalker(out IntPtr w);
        [PreserveSig] int GetRawViewCondition(out IntPtr c);
        [PreserveSig] int GetControlViewCondition(out IntPtr c);
        [PreserveSig] int GetContentViewCondition(out IntPtr c);
        [PreserveSig] int CreateCacheRequest(out IntPtr r);
        [PreserveSig] int CreateTrueCondition(out IntPtr c);
        [PreserveSig] int CreateFalseCondition(out IntPtr c);
        [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IntPtr condition);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr id);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IUIAutomationElement? element);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr elements);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr element);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr elements);
        [PreserveSig] int BuildUpdatedCache(IntPtr cache, out IntPtr element);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    private readonly HookProc mouseProc, keyProc;
    private readonly Func<bool> enabled;
    private readonly Action<IntPtr, bool> onPaste;
    private IntPtr mouseHook, keyHook;

    // What the worker found; read by the hooks. Plain fields behind one lock-free snapshot object.
    private sealed record Found(IntPtr Menu, int Left, int Top, int Right, int Bottom, IntPtr Explorer, bool Desktop, long Expires);
    private volatile Found? found;
    private int searching;
    private IntPtr pressedOn;       // a click we are swallowing: its button-up must be swallowed too
    private Found? swallowing;

    /// <param name="enabled">Asked on every click, so switching the feature off takes effect at once.</param>
    /// <param name="onPaste">Called (from inside the hook; return fast) with the Explorer window and whether it is the desktop.</param>
    public MenuPasteHook(Func<bool> enabled, Action<IntPtr, bool> onPaste)
    {
        this.enabled = enabled;
        this.onPaste = onPaste;
        mouseProc = MouseCallback;
        keyProc = KeyCallback;
        mouseHook = SetWindowsHookEx(WhMouseLl, mouseProc, GetModuleHandle(null), 0);
        keyHook = SetWindowsHookEx(WhKeyboardLl, keyProc, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero) Log.Write("Could not install the menu Paste mouse hook, error " + Marshal.GetLastWin32Error());
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private Found? Current()
    {
        var f = found;
        if (f == null) return null;
        if (Environment.TickCount64 > f.Expires || !IsWindowVisible(f.Menu)) { found = null; return null; }
        return f;
    }

    // ---- hooks -------------------------------------------------------------------------------------------------

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                int msg = (int)wParam;
                var m = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                if ((m.Flags & LlmhfInjected) == 0 && enabled())
                {
                    if (msg == WmRButtonUp) BeginSearch();
                    else if (msg == WmLButtonDown)
                    {
                        var f = Current();
                        if (f != null && m.X >= f.Left && m.X < f.Right && m.Y >= f.Top && m.Y < f.Bottom && IsClipboardFormatAvailable(CfHDrop))
                        {
                            swallowing = f;
                            return (IntPtr)1; // the press is ours; the release finishes it below
                        }
                    }
                    else if (msg == WmLButtonUp && swallowing is { } taken)
                    {
                        swallowing = null;
                        found = null;
                        Claim(taken);
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Write("Menu Paste mouse hook error", ex); swallowing = null; }
        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && (int)wParam == WmKeyDown)
            {
                var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (k.VkCode == VkP && (k.Flags & LlkhfInjected) == 0 && k.Extra != OwnMarker && !Down(VkControl) && !Down(VkMenu) && enabled()
                    && Current() is { } f && IsClipboardFormatAvailable(CfHDrop))
                {
                    found = null;
                    Claim(f);
                    return (IntPtr)1; // the P accelerator of "Paste"
                }
            }
        }
        catch (Exception ex) { Log.Write("Menu Paste key hook error", ex); }
        return CallNextHookEx(keyHook, code, wParam, lParam);
    }

    /// <summary>Closes the menu and tells the app. Runs inside the hook, so it only posts input and calls back.</summary>
    private void Claim(Found f)
    {
        Input Key(ushort vk, bool up) => new() { Type = 1, U = new InputUnion { Ki = new KeybdInput { Vk = vk, Flags = up ? 2u : 0u, Extra = OwnMarker } } };
        var esc = new[] { Key(VkEscape, false), Key(VkEscape, true) };
        SendInput((uint)esc.Length, esc, Marshal.SizeOf<Input>());
        onPaste(f.Explorer, f.Desktop);
    }

    // ---- finding the menu --------------------------------------------------------------------------------------

    private void BeginSearch()
    {
        IntPtr window = GetForegroundWindow();
        string cls = ClassOf(window);
        bool desktop = cls is "Progman" or "WorkerW";
        if (cls != "CabinetWClass" && !desktop) return;
        if (Interlocked.Exchange(ref searching, 1) == 1) return; // already looking
        Task.Run(() =>
        {
            try { Search(window, desktop); }
            catch (Exception ex) { Log.Write("Menu Paste search failed", ex); }
            finally { Volatile.Write(ref searching, 0); }
        });
    }

    private void Search(IntPtr explorer, bool desktop)
    {
        CoInitializeEx(IntPtr.Zero, 0); // multithreaded; harmless if the thread already has a mode
        GetWindowThreadProcessId(explorer, out uint explorerPid);
        var automation = (IUIAutomation)new CUIAutomation();

        for (int attempt = 0; attempt < 14; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 120 : 100);
            var candidates = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == explorerPid && IsWindowVisible(h))
                {
                    string c = ClassOf(h);
                    if (c == "#32768" || c.StartsWith("Microsoft.UI.Content", StringComparison.Ordinal) || c.Contains("Popup", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(h);
                }
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr menu in candidates)
            {
                if (automation.ElementFromHandle(menu, out var root) != 0 || root == null) continue;
                var rect = FindRect(automation, root, PasteNames);
                if (rect == null) continue;
                bool background = BackgroundMarkers.Any(n => FindRect(automation, root, new[] { n }) != null);
                if (!background) return; // an item menu: Paste there means "into that folder", which Windows resolves
                var r = rect.Value;
                found = new Found(menu, r.l, r.t, r.r, r.b, explorer, desktop, Environment.TickCount64 + 60_000);
                return;
            }
            if (!Down(0x02) && attempt > 3 && candidates.Count == 0) return; // no menu came up
        }
    }

    private static (int l, int t, int r, int b)? FindRect(IUIAutomation automation, IUIAutomationElement root, string[] names)
    {
        foreach (string name in names)
        {
            if (automation.CreatePropertyCondition(30005, name, out IntPtr condition) != 0 || condition == IntPtr.Zero) continue; // 30005 = Name
            try
            {
                if (root.FindFirst(7, condition, out var item) != 0 || item == null) continue; // 7 = the whole subtree
                if (item.GetCurrentPropertyValue(30001, out object value) != 0 || value is not double[] { Length: 4 } box) continue; // 30001 = BoundingRectangle
                if (box[2] < 4 || box[3] < 4) continue;
                return ((int)box[0], (int)box[1], (int)(box[0] + box[2]), (int)(box[1] + box[3]));
            }
            finally { Marshal.Release(condition); }
        }
        return null;
    }

    public void Dispose()
    {
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        mouseHook = keyHook = IntPtr.Zero;
    }
}
