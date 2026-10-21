namespace Diffract.Git;

/// <summary>
/// Which git.exe to start. Git for Windows puts a launcher on PATH (<c>cmd\git.exe</c>) that starts the real git
/// (<c>mingw64\bin\git.exe</c>) as a second process. On a busy Windows machine that second start costs 200 ms or
/// more per call, so Diffract starts the real git itself. See docs/findings/history.md.
/// </summary>
/// <param name="Executable">A full path, or "git" to let Windows search PATH as before.</param>
/// <param name="PathPrefix">
/// Folders to put in front of PATH for git, as the launcher does: <c>mingw64\bin</c> and <c>usr\bin</c>. The real git
/// sets HOME, MSYSTEM and PLINK_PROTOCOL itself, so PATH is the only difference.
/// </param>
public sealed record GitInstallation(string Executable, string? PathPrefix)
{
    /// <summary>Whatever "git" on PATH is, started the way it always was.</summary>
    public static GitInstallation OnPath { get; } = new("git", null);

    /// <summary>The installation Windows would start for "git" on this machine.</summary>
    public static GitInstallation ForThisMachine() => Find(Environment.GetEnvironmentVariable("PATH"), File.Exists);

    /// <summary>
    /// Looks at the first git.exe on PATH, as Windows would. When it is one of Git for Windows' launchers
    /// (<c>cmd\git.exe</c> or <c>bin\git.exe</c>) or the real git itself, the real git is used. Anything else, such as a
    /// package manager's shim, is left alone.
    /// </summary>
    public static GitInstallation Find(string? pathVariable, Func<string, bool> fileExists)
    {
        foreach (var entry in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = Path.TrimEndingDirectorySeparator(entry.Trim('"'));
            if (!Path.IsPathFullyQualified(folder) || !fileExists(Path.Combine(folder, "git.exe")))
                continue;

            // The first git.exe on PATH is the one "git" means, so only it is looked at.
            // mingw64\bin before bin, since it ends in bin too.
            var root = Path.GetFileName(folder).ToLowerInvariant() switch
            {
                _ when folder.EndsWith(Path.Combine("mingw64", "bin"), StringComparison.OrdinalIgnoreCase) => Path.GetDirectoryName(Path.GetDirectoryName(folder)),
                "cmd" or "bin" => Path.GetDirectoryName(folder),
                _ => null,
            };
            var real = root is null ? null : Path.Combine(root, "mingw64", "bin", "git.exe");
            return real is not null && fileExists(real)
                ? new GitInstallation(real, string.Join(Path.PathSeparator, Path.Combine(root!, "mingw64", "bin"), Path.Combine(root!, "usr", "bin")))
                : OnPath;
        }
        return OnPath;
    }

    /// <summary>Runner options that start this git.</summary>
    public GitRunnerOptions Apply(GitRunnerOptions options) => options with { GitExecutable = Executable, PathPrefix = PathPrefix };
}
