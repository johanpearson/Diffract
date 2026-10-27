namespace Diffract.Git.Changes;

/// <param name="Message">From the commit box. Only its first line is used; without one, git writes "WIP on &lt;branch&gt;".</param>
/// <param name="IncludeUntracked">Stash new files too, not only changes to files git tracks.</param>
public sealed record StashRequest(string? Message, bool IncludeUntracked);

/// <summary>A stash that was made: its commit, and what happened in plain words.</summary>
public sealed record StashSaved(string CommitId, string Message);

/// <summary>What applying a stash did. With conflicts, the stash is kept and the worktree lists the conflicted files.</summary>
public sealed record StashApplied(bool Conflicts, string Message);

/// <summary>
/// Saves and applies stashes in one worktree, through a changes runner. Neither throws work away: a stash keeps what it
/// takes, and applying keeps the stash. Dropping is a cleanup, with a backup tag and undo, so it is not here.
/// </summary>
public sealed class StashWriter
{
    private static readonly string[] StashTip = ["rev-parse", "--verify", "--quiet", "refs/stash"];

    private static readonly string[] Unmerged = ["diff", "--name-only", "--diff-filter=U", "-z"];

    private readonly GitRunner _changes;
    private readonly GitRunner _reads;
    private readonly IndexLockRetry _retry;

    public StashWriter(string worktree, GitRunnerOptions runnerOptions)
    {
        _changes = new GitRunner(worktree, runnerOptions with { ChangesOnly = true });
        _reads = new GitRunner(worktree, runnerOptions with { ChangesOnly = false, ReadOnly = true });
        _retry = new IndexLockRetry(runnerOptions.TimeProvider);
    }

    /// <summary>
    /// Git answers "No local changes to save" with exit code 0 and makes no stash, so whether one was made is read
    /// from refs/stash before and after. Three git calls.
    /// </summary>
    public async Task<StashSaved> PushAsync(StashRequest request, CancellationToken ct)
    {
        var before = await StashTipAsync(ct).ConfigureAwait(false);
        var result = await _retry.RunAsync(_changes, new GitCommandLine(PushArguments(request), []), [1], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ChangeRefusedException("Git did not stash, so nothing changed. This is what git said:", result.StandardError);
        if (await StashTipAsync(ct).ConfigureAwait(false) is not { } after || after == before)
            throw new ChangeRefusedException(request.IncludeUntracked ? "There is nothing to stash." : "There is nothing to stash. New files are only stashed with \"Include new files\".");
        return new StashSaved(after, "Stashed your changes. The stash keeps them until you drop it.");
    }

    /// <summary>
    /// Applies the stash by its commit and keeps it. Git returns 1 both for conflicts and for a refusal, such as
    /// uncommitted changes in the way, so conflicts are read from the index afterwards. A new file in the way is
    /// refused with 128.
    /// </summary>
    public async Task<StashApplied> ApplyAsync(string commit, CancellationToken ct)
    {
        if (!ObjectId.IsValid(commit))
            throw new ArgumentException($"Not a full commit id: {commit}", nameof(commit));
        var result = await _retry.RunAsync(_changes, new GitCommandLine(["stash", "apply", commit], []), [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
            return new StashApplied(false, "Applied the stash. It is still in the list.");
        if (await HasConflictsAsync(ct).ConfigureAwait(false))
            return new StashApplied(true, "Applied the stash with conflicts. It is still in the list, and Changes lists the conflicted files.");
        throw new ChangeRefusedException("Git did not apply the stash, so nothing changed. This is what git said:", result.StandardError);
    }

    /// <summary>The message as its own argument, after -m, so git never reads it as an option.</summary>
    private static string[] PushArguments(StashRequest request)
    {
        var subject = (request.Message ?? "").Split('\n')[0].Trim();
        string[] push = request.IncludeUntracked ? ["stash", "push", "--include-untracked"] : ["stash", "push"];
        return subject.Length == 0 ? push : [.. push, "-m", subject];
    }

    private async Task<string?> StashTipAsync(CancellationToken ct)
    {
        var result = await _reads.RunAsync(StashTip, [1], ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private async Task<bool> HasConflictsAsync(CancellationToken ct) =>
        (await _reads.RunAsync(Unmerged, ct).ConfigureAwait(false)).StandardOutput.Length > 0;
}
