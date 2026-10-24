namespace Diffract.Git.Refs;

/// <summary>What is checked out in one worktree: a branch, or a detached commit.</summary>
/// <param name="Branch">Such as "main". Null when HEAD is detached.</param>
/// <param name="Commit">The commit at HEAD. Null before the first commit.</param>
public sealed record HeadState(string? Branch, string? Commit)
{
    private const string Heads = "refs/heads/";

    /// <summary>Two reads, side by side: the branch HEAD points at, and the commit it resolves to.</summary>
    public static async Task<HeadState> ReadAsync(GitRunner runner, CancellationToken ct)
    {
        var branch = runner.RunAsync(["symbolic-ref", "--quiet", "HEAD"], [1], ct);
        var commit = runner.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], [1], ct);
        await Task.WhenAll(branch, commit).ConfigureAwait(false);
        return new HeadState(BranchOf(await branch.ConfigureAwait(false)), Line(await commit.ConfigureAwait(false)));
    }

    private static string? BranchOf(GitResult result) =>
        Line(result) is { } name && name.StartsWith(Heads, StringComparison.Ordinal) ? name[Heads.Length..] : null;

    private static string? Line(GitResult result) => result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
}
