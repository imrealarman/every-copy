using System.Text.Json;
using System.Text.Json.Serialization;
using EveryCopy.Models;

namespace EveryCopy.Services;

/// <summary>A log of finished transfers kept between runs, newest first, so the user can see what was copied and whether it worked.</summary>
public static class HistoryStore
{
    public const int MaxEntries = 300;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object Gate = new();

    public sealed record Entry(
        DateTime FinishedLocal, string From, string To, List<string> Sources, string Destination,
        TransferMode Mode, CollisionPolicy Collision, bool Verify, int Threads,
        JobState State, int Files, int Failed, long Bytes, double Seconds, string? Error);

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryCopy", "history.json");

    public static Entry From(CopyJob job) => new(
        DateTime.Now, job.FromName, job.ToName, job.Sources.ToList(), job.Destination,
        job.Options.Mode, job.Options.Collision, job.Options.Verify, job.Options.Threads,
        job.State, job.TotalFiles, job.FailedFiles, job.TotalBytes,
        job.FinishedUtc > job.StartedUtc ? (job.FinishedUtc - job.StartedUtc).TotalSeconds : 0, job.FatalError);

    public static void Add(Entry entry, string? path = null)
    {
        try
        {
            lock (Gate)
            {
                var all = Load(path);
                all.Insert(0, entry);
                if (all.Count > MaxEntries) all.RemoveRange(MaxEntries, all.Count - MaxEntries);
                Write(all, path);
            }
        }
        catch (Exception ex) { Log.Write("Could not save the history", ex); }
    }

    public static List<Entry> Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read the history", ex);
            return new();
        }
    }

    public static void Clear(string? path = null)
    {
        try { lock (Gate) File.Delete(path ?? FilePath); }
        catch (Exception ex) { Log.Write("Could not clear the history", ex); }
    }

    private static void Write(List<Entry> all, string? path)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
        File.Move(temp, path, true); // a crash mid-write cannot leave half a history behind
    }
}
