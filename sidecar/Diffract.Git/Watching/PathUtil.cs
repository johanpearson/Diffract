namespace Diffract.Git.Watching;

/// <summary>Windows path comparisons: full paths, case-insensitive, no trailing separator.</summary>
internal static class PathUtil
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsSameOrInside(string path, string folder)
    {
        var (p, f) = (Normalize(path), Normalize(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path below <paramref name="folder"/> with forward slashes, or null when it is not inside it.</summary>
    public static string? Relative(string path, string folder)
    {
        var (p, f) = (Normalize(path), Normalize(folder));
        if (!p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return p[(f.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
    }
}
