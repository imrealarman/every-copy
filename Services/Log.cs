namespace EveryCopy.Services;

/// <summary>Appends problems to %AppData%\EveryCopy\log.txt so a failed transfer can be looked into afterwards.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryCopy", "log.txt");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{(ex == null ? "" : "\n    " + ex)}\n");
            }
        }
        catch { /* logging must never be the thing that fails */ }
    }
}
