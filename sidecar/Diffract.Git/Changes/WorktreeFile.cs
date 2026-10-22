using Diffract.Git.Files;

namespace Diffract.Git.Changes;

/// <summary>A file in a worktree, by a path from <c>git status</c>. Only ever inside the worktree, never in a .git folder.</summary>
public static class WorktreeFile
{
    /// <summary>
    /// The full path of <paramref name="path"/> in <paramref name="worktree"/>. A path that is rooted, climbs out with
    /// "..", or goes through a .git folder is refused, so a path from outside can only ever read the worktree's own files.
    /// </summary>
    public static string Resolve(string worktree, string path)
    {
        if (!IsRelativeAndPlain(path))
            throw new ArgumentException($"Not a path inside the worktree: '{path}'.", nameof(path));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new ArgumentException($"Not a path inside the worktree: '{path}'.", nameof(path));
    }

    private static bool IsRelativeAndPlain(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Split('/', '\\').Any(IsRefused);

    /// <summary>Git never tracks a path with these, so no status entry has one.</summary>
    private static bool IsRefused(string segment) =>
        segment is "." or ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file as it is on disk, or null when there is none. Read while editors and agents may be writing it, so it
    /// never blocks them. A symbolic link is read as git stores it: the path it points to, not what is there.
    /// </summary>
    public static async Task<FileContent?> ReadAsync(string fullPath, CancellationToken ct, int maxBytes = FileContentReader.MaxFileBytes)
    {
        var info = new FileInfo(fullPath);
        if (info.LinkTarget is { } target)
            return new FileContent(FileContentKind.Text, target);
        if (!info.Exists)
            return null;

        using var reader = new FileContentReader(maxBytes);
        if (reader.IsTooLarge(info.Length))
            return reader.TooLarge();

        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            await reader.ReadAsync(stream, ct).ConfigureAwait(false);
            return reader.Content;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the check and the read.
            return null;
        }
    }
}
