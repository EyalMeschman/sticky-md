namespace StickyMD.Core.Tests.TestSupport;

/// <summary>A unique temp directory that deletes itself on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "stickymd-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string WriteFile(string name, string content)
    {
        var full = File(name);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a watcher may still hold a handle; harmless */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }
}
