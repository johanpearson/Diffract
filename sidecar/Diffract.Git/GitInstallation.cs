namespace Diffract.Git;

/// <summary>
/// Which git.exe to start. Git for Windows puts a launcher on PATH (<c>cmd\git.exe</c>) that starts the real git
/// (<c>ucrt64\bin\git.exe</c>, or <c>mingw64\bin\git.exe</c> before 2.56) as a second process. On a busy Windows
/// machine that second start costs 200 ms or more per call, so Diffract starts the real git itself. See
/// docs/findings/history.md.
/// </summary>
/// <param name="Executable">A full path, or "git" to let Windows search PATH as before.</param>
/// <param name="PathPrefix">
/// Folders to put in front of PATH for git, as the launcher does: the real git's <c>bin</c> folder and <c>usr\bin</c>.
/// The real git sets HOME, MSYSTEM and PLINK_PROTOCOL itself, so PATH is the only difference.
/// </param>
public sealed record GitInstallation(string Executable, string? PathPrefix)
{
    /// <summary>
    /// The folders Git for Windows keeps the real git in, newest first. It moved from mingw64 to ucrt64 in 2.56, and
    /// the build for ARM uses clangarm64.
    /// </summary>
    public static IReadOnlyList<string> Environments { get; } = ["ucrt64", "mingw64", "clangarm64"];

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
            var root = InstallationRoot(folder);
            return root is null ? OnPath : RealGit(root, fileExists);
        }
        return OnPath;
    }

    /// <summary>Git for Windows' own folder, when <paramref name="folder"/> is one of its folders with a git.exe.</summary>
    private static string? InstallationRoot(string folder)
    {
        // An environment's bin before plain bin, since it ends in bin too.
        if (Environments.Any(env => folder.EndsWith(Path.Combine(env, "bin"), StringComparison.OrdinalIgnoreCase)))
            return Path.GetDirectoryName(Path.GetDirectoryName(folder));
        return Path.GetFileName(folder).ToLowerInvariant() is "cmd" or "bin" ? Path.GetDirectoryName(folder) : null;
    }

    private static GitInstallation RealGit(string root, Func<string, bool> fileExists)
    {
        foreach (var env in Environments)
        {
            var bin = Path.Combine(root, env, "bin");
            var real = Path.Combine(bin, "git.exe");
            if (fileExists(real))
                return new GitInstallation(real, string.Join(Path.PathSeparator, bin, Path.Combine(root, "usr", "bin")));
        }
        return OnPath;
    }

    /// <summary>Runner options that start this git.</summary>
    public GitRunnerOptions Apply(GitRunnerOptions options) => options with { GitExecutable = Executable, PathPrefix = PathPrefix };
}
