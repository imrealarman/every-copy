using System.Text.Json;
using System.Text.Json.Serialization;
using EveryCopy.Models;

namespace EveryCopy.Services;

/// <summary>
/// Remembers the transfers that had not finished, so closing EveryCopy, a crash or a restart does not lose them.
/// They come back as resumed jobs: files already copied intact are skipped and half-copied ones are finished.
/// </summary>
public static class JobStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record StoredJob(List<string> Sources, string Destination, TransferMode Mode, CollisionPolicy Collision, bool Verify, int Threads);

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryCopy", "queue.json");

    public static void Save(IEnumerable<CopyJob> jobs, string? path = null)
    {
        try
        {
            path ??= FilePath;
            var unfinished = jobs.Where(j => !j.IsFinished)
                .Select(j => new StoredJob(j.Sources.ToList(), j.Destination, j.Options.Mode, j.Options.Collision, j.Options.Verify, j.Options.Threads))
                .ToList();
            if (unfinished.Count == 0) { File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(unfinished, Json));
        }
        catch (Exception ex) { Log.Write("Could not save the queue", ex); }
    }

    public static List<StoredJob> Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<StoredJob>>(File.ReadAllText(path), Json)?
                .Where(j => j.Sources is { Count: > 0 } && !string.IsNullOrWhiteSpace(j.Destination)).ToList() ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read the saved queue", ex);
            return new();
        }
    }
}
