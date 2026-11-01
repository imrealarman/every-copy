using System.Runtime.InteropServices;

namespace EveryCopy.Services;

/// <summary>Shows the transfer's progress on EveryCopy's taskbar button, the way Explorer's copy does.</summary>
public sealed class TaskbarProgress
{
    public enum State { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] private class CTaskbarList { }

    // The vtable runs ITaskbarList (3-7), ITaskbarList2 (8), then ITaskbarList3; the placeholders keep the slots in order.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
    }

    private readonly IntPtr window;
    private ITaskbarList3? taskbar;
    private bool failed;
    private (State State, int Permille) last = (State.None, -1);

    public TaskbarProgress(IntPtr window) => this.window = window;

    public void Set(State state, double percent)
    {
        if (failed) return;
        int permille = (int)Math.Clamp(percent * 10, 0, 1000);
        if (last == (state, permille)) return;
        last = (state, permille);

        try
        {
            if (taskbar == null)
            {
                taskbar = (ITaskbarList3)new CTaskbarList();
                taskbar.HrInit();
            }
            taskbar.SetProgressState(window, (int)state);
            if (state is State.Normal or State.Paused or State.Error) taskbar.SetProgressValue(window, (ulong)permille, 1000);
        }
        catch (Exception ex)
        {
            failed = true; // a taskbar that will not talk to us is not worth retrying on every tick
            Log.Write("Taskbar progress is not available", ex);
        }
    }
}
