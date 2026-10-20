using Diffract.Git;

namespace Diffract.Fixtures;

/// <summary>Commit date for fixture repos. Shared between a repo and its worktrees.</summary>
public sealed class FixtureClock
{
    public DateTimeOffset Now { get; set; } = IsolatedGit.DefaultDate;
}

/// <summary>
/// A git repo or worktree inside a <see cref="TempDirectory"/> that tests may write to.
/// Commits and tags are dated with <see cref="Clock"/>.
/// </summary>
public sealed class FixtureRepo(TempDirectory temp, string path, FixtureClock clock)
{
    public string Path { get; } = path;

    public FixtureClock Clock { get; } = clock;

    public static async Task<FixtureRepo> InitAsync(TempDirectory temp, string name, string mainBranch = "main", bool bare = false)
    {
        var path = temp.Combine(name);
        Directory.CreateDirectory(path);
        var repo = new FixtureRepo(temp, path, new FixtureClock());
        await repo.GitAsync(bare ? ["init", "-q", "--bare", "-b", mainBranch] : ["init", "-q", "-b", mainBranch]);
        return repo;
    }

    /// <summary>Another folder in the same temp folder, such as a linked worktree, sharing this clock.</summary>
    public FixtureRepo At(string otherPath) => new(temp, otherPath, Clock);

    public Task<GitResult> GitAsync(params string[] args) =>
        IsolatedGit.WritableRunner(temp, Path, Clock.Now).RunAsync(args, CancellationToken.None);

    /// <summary>Writes a file, commits it and returns the new commit id.</summary>
    public async Task<string> CommitAsync(string file, string content, string message)
    {
        WriteFile(file, content);
        await GitAsync("add", "--", file);
        await GitAsync("commit", "-q", "-m", message);
        return await RevParseAsync("HEAD");
    }

    public async Task<string> RevParseAsync(string rev) =>
        (await GitAsync("rev-parse", rev)).StandardOutput.Trim();

    public void WriteFile(string file, string content, DateTimeOffset? modified = null)
    {
        var full = System.IO.Path.Combine(Path, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (modified is { } m)
            File.SetLastWriteTimeUtc(full, m.UtcDateTime);
    }
}
