using Diffract.Git.Snapshot;

namespace Diffract.Fixtures;

/// <summary>A uniquely named folder under the system temp folder, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    private readonly bool _deleteOnDispose;

    public TempDirectory()
        : this(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "diffract-tests", Guid.NewGuid().ToString("N")[..12]), deleteOnDispose: true)
    {
    }

    private TempDirectory(string path, bool deleteOnDispose)
    {
        Path = path;
        _deleteOnDispose = deleteOnDispose;
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// A folder someone picked for fixture repos they want to inspect by hand. It is kept on dispose.
    /// Refuses a folder that already has content or sits inside a git repo, so fixtures never mix with real work.
    /// </summary>
    public static TempDirectory ForFixtureOutput(string path)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new InvalidOperationException($"The fixture folder must be empty or not exist yet: {full}");

        for (var folder = new DirectoryInfo(full); folder is not null; folder = folder.Parent)
        {
            if (folder.Exists && GitDirectory.HasGitEntry(folder.FullName))
                throw new InvalidOperationException($"The fixture folder is inside the git repo at {folder.FullName}. Pick a folder outside any repo.");
        }

        return new TempDirectory(full, deleteOnDispose: false);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        if (!_deleteOnDispose || !Directory.Exists(Path))
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
