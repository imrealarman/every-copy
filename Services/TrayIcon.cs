using System.Runtime.InteropServices;

namespace EveryCopy.Services;

/// <summary>
/// The notification-area icon that keeps EveryCopy (and its Ctrl+V takeover) alive after the window is closed.
/// Built straight on the Win32 shell API: a hidden message-only window receives the icon's mouse events.
/// Create it on the UI thread so its callbacks run there.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000, TrayMessage = WmApp + 1;
    private const uint WmLButtonUp = 0x202, WmRButtonUp = 0x205, WmDestroy = 0x2;
    private const uint NimAdd = 0, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NimModify = 1;
    private const uint MfString = 0, MfSeparator = 0x800, MfChecked = 8;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 2, TpmBottomAlign = 0x20;
    private const int HwndMessage = -3;
    private const int CmdOpen = 1, CmdIntercept = 2, CmdExit = 3;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Size, Style;
        public WndProc? Proc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    private readonly Action open, exit;
    private readonly Func<bool> intercepting;
    private readonly Action<bool> setIntercepting;
    private readonly WndProc proc; // held in a field so the GC cannot free it while Windows calls it
    private readonly IntPtr hwnd;
    private readonly IntPtr icon;
    private readonly uint taskbarCreated;
    private bool added;

    public TrayIcon(string iconPath, Action open, Func<bool> intercepting, Action<bool> setIntercepting, Action exit)
    {
        this.open = open;
        this.intercepting = intercepting;
        this.setIntercepting = setIntercepting;
        this.exit = exit;
        proc = HandleMessage;
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        string className = "EveryCopy.Tray." + Guid.NewGuid().ToString("N");
        IntPtr instance = GetModuleHandleW(null);
        var cls = new WndClass { Size = (uint)Marshal.SizeOf<WndClass>(), Proc = proc, Instance = instance, ClassName = className };
        if (RegisterClassExW(ref cls) == 0) throw new InvalidOperationException("Could not register the tray window class.");

        hwnd = CreateWindowExW(0, className, "EveryCopy tray", 0, 0, 0, 0, 0, (IntPtr)HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the tray window.");

        icon = File.Exists(iconPath) ? LoadImageW(IntPtr.Zero, iconPath, 1, 16, 16, 0x10) : IntPtr.Zero;
        Add();
    }

    private NotifyIconData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Hwnd = hwnd,
        Id = 1,
        Flags = flags,
        CallbackMessage = TrayMessage,
        Icon = icon,
        Tip = "EveryCopy",
        Info = "",
        InfoTitle = "",
    };

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip);
        added = Shell_NotifyIconW(NimAdd, ref data);
    }

    public void Notify(string title, string text)
    {
        var data = Data(NifInfo);
        data.InfoTitle = title;
        data.Info = text;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private IntPtr HandleMessage(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == TrayMessage)
            {
                uint mouse = (uint)(long)lParam & 0xFFFF;
                if (mouse == WmLButtonUp) open();
                else if (mouse == WmRButtonUp) ShowMenu();
                return IntPtr.Zero;
            }
            if (msg == taskbarCreated) { Add(); return IntPtr.Zero; } // Explorer restarted: put the icon back
        }
        catch (Exception ex) { Log.Write("Tray message failed", ex); }
        return DefWindowProcW(window, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MfString, (UIntPtr)CmdOpen, "Open EveryCopy");
            AppendMenuW(menu, MfString | (intercepting() ? MfChecked : 0), (UIntPtr)CmdIntercept, "Use EveryCopy for Ctrl+V in Explorer");
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(menu, MfString, (UIntPtr)CmdExit, "Exit");

            GetCursorPos(out var p);
            SetForegroundWindow(hwnd); // required, or the menu does not close when the user clicks elsewhere
            int command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton | TpmBottomAlign, p.X, p.Y, 0, hwnd, IntPtr.Zero);
            switch (command)
            {
                case CmdOpen: open(); break;
                case CmdIntercept: setIntercepting(!intercepting()); break;
                case CmdExit: exit(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NimDelete, ref data);
            added = false;
        }
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }
}
