using System.Diagnostics;

namespace EveryCopy.Services;

/// <summary>Measures how fast a folder's drive accepts data, so the user can see whether the destination is the bottleneck.</summary>
public static class Benchmark
{
    private const int Chunk = 4 * 1024 * 1024;

    /// <summary>
    /// Writes a temporary file of <paramref name="bytes"/> into <paramref name="folder"/>, forces it to disk, and returns the
    /// write speed in bytes per second. The file is always removed. Throws IOException if the folder cannot be written.
    /// </summary>
    public static double MeasureWrite(string folder, long bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(PathUtil.Long(folder));
        string file = Path.Combine(folder, ".everycopy-speedtest-" + Guid.NewGuid().ToString("N") + ".tmp");
        var data = new byte[Chunk];
        new Random(7).NextBytes(data); // random, so compression on the drive or share cannot flatter the result

        try
        {
            var clock = new Stopwatch();
            using (var fs = new FileStream(PathUtil.Long(file), FileMode.CreateNew, FileAccess.Write, FileShare.None, Chunk, FileOptions.SequentialScan))
            {
                clock.Start();
                for (long written = 0; written < bytes; written += Chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    fs.Write(data, 0, (int)Math.Min(Chunk, bytes - written));
                }
                fs.Flush(true); // until it is really on the disk, not just in the cache
                clock.Stop();
            }
            return bytes / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        }
        finally
        {
            try { File.Delete(PathUtil.Long(file)); } catch { /* a temp file we could not remove is named so it can be spotted */ }
        }
    }
}
