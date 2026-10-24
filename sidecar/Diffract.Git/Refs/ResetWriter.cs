using Diffract.Git.Changes;

namespace Diffract.Git.Refs;

/// <summary>Both keep every change: soft keeps the index as it is, mixed puts the changes back as unstaged.</summary>
public enum ResetMode
{
    Soft,
    Mixed,
}

/// <summary>A reset that happened: the branch moved from one commit to another. Pass it back to undo it.</summary>
public sealed record ResetResult
{
    public required string Branch { get; init; }

    public required string OldCommit { get; init; }

    public required string NewCommit { get; init; }

    public required ResetMode Mode { get; init; }
}

/// <summary>
/// Moves the current branch of one worktree to another commit with <c>git reset --soft</c> or <c>--mixed</c>, through
/// a changes runner. Neither touches the working tree. Undo moves it back, but only while it is still where the reset
/// left it, so it never undoes a commit, pull or reset that came after.
/// </summary>
public sealed class ResetWriter
{
    private static readonly Dictionary<ResetMode, string> Options = new()
    {
        [ResetMode.Soft] = "--soft",
        [ResetMode.Mixed] = "--mixed",
    };

    private readonly GitRunner _changes;
    private readonly GitRunner _reads;
    private readonly IndexLockRetry _retry;

    public ResetWriter(string worktree, GitRunnerOptions runnerOptions)
    {
        _changes = new GitRunner(worktree, runnerOptions with { ChangesOnly = true });
        _reads = new GitRunner(worktree, runnerOptions with { ChangesOnly = false, ReadOnly = true });
        _retry = new IndexLockRetry(runnerOptions.TimeProvider);
    }

    public async Task<ResetResult> ResetAsync(string commit, ResetMode mode, CancellationToken ct)
    {
        FullId(commit);
        var head = await HeadState.ReadAsync(_reads, ct).ConfigureAwait(false);
        if (head is not { Branch: { } branch, Commit: { } current })
            throw new ChangeRefusedException("No branch is checked out here, so there is no branch to reset.");
        if (current == commit)
            throw new ChangeRefusedException($"{branch} is already at {ShortId.Of(commit)}.");
        await MoveAsync(commit, mode, ct).ConfigureAwait(false);
        return new ResetResult { Branch = branch, OldCommit = current, NewCommit = commit, Mode = mode };
    }

    /// <summary>Moves the branch back to where it was, the same way. Answers with that move, from new to old.</summary>
    public async Task<ResetResult> UndoAsync(ResetResult reset, CancellationToken ct)
    {
        FullId(reset.OldCommit);
        var head = await HeadState.ReadAsync(_reads, ct).ConfigureAwait(false);
        if (head.Branch != reset.Branch || head.Commit != reset.NewCommit)
            throw new ChangeRefusedException($"{reset.Branch} is no longer where the reset left it, so undo did nothing. Something has changed it since.");
        await MoveAsync(reset.OldCommit, reset.Mode, ct).ConfigureAwait(false);
        return reset with { OldCommit = reset.NewCommit, NewCommit = reset.OldCommit };
    }

    /// <summary>A reset goes to a commit by its full id, never to a revision such as "HEAD~1".</summary>
    private static void FullId(string commit)
    {
        if (!ObjectId.IsValid(commit))
            throw new ArgumentException($"Not a full commit id: {commit}", nameof(commit));
    }

    private async Task MoveAsync(string commit, ResetMode mode, CancellationToken ct)
    {
        var result = await _retry.RunAsync(_changes, new GitCommandLine(["reset", Options[mode], commit], []), [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ChangeRefusedException("Git did not reset, so nothing changed. This is what git said:", result.StandardError);
    }
}
