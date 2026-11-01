namespace EveryCopy.Models;

/// <summary>A file or folder waiting in the "new transfer" list.</summary>
public sealed class SourceEntry
{
    public SourceEntry(string path)
    {
        Path = path;
        IsFolder = Directory.Exists(path);
    }

    public string Path { get; }
    public bool IsFolder { get; }
    public string Name => string.IsNullOrEmpty(System.IO.Path.GetFileName(Path.TrimEnd('\\'))) ? Path : System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public string Location => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\')) ?? "";
    public string Glyph => IsFolder ? "" : "";
}
