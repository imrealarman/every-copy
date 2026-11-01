using System.Runtime.InteropServices;

namespace EveryCopy.Services;

/// <summary>Hands a transfer to Windows' own copy engine (the Explorer copy dialog), for people who want it for a particular copy.</summary>
public static class NativeShell
{
    private const uint FoMove = 1, FoCopy = 2;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int ShFileOperation(ref ShFileOpStruct op);

    /// <summary>
    /// Copies or moves with the Windows shell and blocks until it is done. Returns null on success (or when the user
    /// cancelled in Windows' dialog), otherwise a message. <paramref name="silent"/> hides Windows' dialogs; tests use it.
    /// </summary>
    public static string? Transfer(IReadOnlyList<string> sources, string destination, bool move, IntPtr owner, bool silent = false)
    {
        var op = new ShFileOpStruct
        {
            Hwnd = owner,
            Func = move ? FoMove : FoCopy,
            From = string.Join('\0', sources) + "\0", // the API wants a double-null-terminated list; the marshaller adds the last null
            To = destination + "\0",
            Flags = (ushort)(FofAllowUndo | (silent ? FofSilent | FofNoConfirmation | FofNoErrorUi : 0)),
        };

        int result = ShFileOperation(ref op);
        if (result == 0 || op.AnyOperationsAborted) return null;
        return $"Windows copy stopped with error 0x{result:X}.";
    }
}
