using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EveryCopy.Services;

/// <summary>
/// File copy through the OS's own CopyFileEx: the same kernel path Explorer uses, so it gets large IO sizes, SMB
/// server-side offload and block cloning on ReFS/Dev Drive for free, and keeps timestamps, attributes and alternate
/// data streams. Progress, pause and cancel all hang off its callback.
/// </summary>
public static class NativeCopy
{
    private const uint FailIfExists = 0x1;
    private const uint Restartable = 0x2;
    private const uint NoBuffering = 0x1000;
    private const uint ProgressContinue = 0, ProgressCancel = 1;
    private const int ErrorRequestAborted = 1235;

    private delegate uint CopyProgressRoutine(long totalSize, long totalTransferred, long streamSize, long streamTransferred,
        uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CopyFileExW")]
    private static extern bool CopyFileEx(string source, string destination, CopyProgressRoutine? progress, IntPtr data, ref int cancel, uint flags);

    /// <summary>Files at least this big skip the system cache, which is faster and keeps the cache for everything else.</summary>
    public const long UnbufferedThreshold = 256L * 1024 * 1024;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>. <paramref name="onBytes"/> receives each chunk's
    /// size; with <paramref name="restartable"/> an interrupted copy leaves a partial file that the next call (overwrite on)
    /// carries on from. The call blocks while <paramref name="gate"/> is closed (paused) and throws OperationCanceledException on cancel.
    /// </summary>
    public static void CopyFile(string source, string destination, bool overwrite, bool unbuffered, bool restartable, Action<long> onBytes,
        CancellationToken ct, ManualResetEventSlim gate)
    {
        long last = 0;
        CopyProgressRoutine callback = (_, transferred, _, _, _, _, _, _, _) =>
        {
            try { gate.Wait(ct); }
            catch (OperationCanceledException) { return ProgressCancel; }
            if (ct.IsCancellationRequested) return ProgressCancel;
            long delta = transferred - last;
            last = transferred;
            if (delta > 0) onBytes(delta);
            return ProgressContinue;
        };

        int cancel = 0;
        uint flags = (overwrite ? 0u : FailIfExists) | (unbuffered ? NoBuffering : 0u) | (restartable ? Restartable : 0u);
        bool ok = CopyFileEx(PathUtil.Long(source), PathUtil.Long(destination), callback, IntPtr.Zero, ref cancel, flags);
        int error = ok ? 0 : Marshal.GetLastWin32Error();
        GC.KeepAlive(callback);

        if (ok) return;
        if (error == ErrorRequestAborted || ct.IsCancellationRequested)
        {
            TryDelete(destination);
            ct.ThrowIfCancellationRequested();
        }
        throw new IOException(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));
    }

    /// <summary>The Win32 error inside an IOException's HRESULT, or 0 when it is not a Win32 one.</summary>
    public static int Win32Code(Exception ex) => ((uint)ex.HResult & 0xFFFF0000u) == 0x80070000u ? ex.HResult & 0xFFFF : 0;

    public static bool IsAlreadyExists(IOException ex) => ex.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7);
    public static bool IsSharingViolation(IOException ex) => ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    private static void TryDelete(string path)
    {
        try { File.Delete(PathUtil.Long(path)); } catch { /* best effort: a half-written file is the only thing left */ }
    }
}
