namespace Diffract.Fixtures;

/// <summary>A uniquely named folder under the system temp folder, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "diffract-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        if (!Directory.Exists(Path))
            return;

        // git marks object files read-only, which makes Directory.Delete fail on Windows.
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A process may still hold a file for a moment. Leftover temp folders are harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
