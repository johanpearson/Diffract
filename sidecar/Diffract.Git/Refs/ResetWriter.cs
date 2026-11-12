using Diffract.Git.Changes;

namespace Diffract.Git.Refs;

/// <summary>
/// Soft keeps the index as it is, mixed puts the changes back as unstaged. Hard throws away every uncommitted change,
/// so it is only made through <c>HardReset</c>, which keeps a copy of each file first.
/// </summary>
public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}

/// <summary>One file a hard reset threw away, as the copy and the disk say.</summary>
/// <param name="Path">Relative to the worktree, with forward slashes.</param>
/// <param name="BackupName">The copy of the file before the reset. Null when there was no file, so Undo deletes it again.</param>
/// <param name="AfterHash">The SHA-256 of the file after the reset. Null when there was no file. Undo needs the file to still match it.</param>
public sealed record KeptFile(string Path, string? BackupName, string? AfterHash);

/// <summary>One path of the index as it was before a hard reset. A mode of 000000 means the path was not in the index.</summary>
public sealed record StagedEntry(string Path, string Mode, string Id);

/// <summary>What a hard reset kept so Undo can bring it back: the files, and what was staged.</summary>
public sealed record KeptWork(IReadOnlyList<KeptFile> Files, IReadOnlyList<StagedEntry> Staged);

/// <summary>A reset that happened: the branch moved from one commit to another. Pass it back to undo it.</summary>
public sealed record ResetResult
{
    public required string Branch { get; init; }

    public required string OldCommit { get; init; }

    public required string NewCommit { get; init; }

    public required ResetMode Mode { get; init; }

    /// <summary>For a hard reset, the uncommitted work that was kept. Null for the other modes.</summary>
    public KeptWork? Kept { get; init; }
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
        if (mode == ResetMode.Hard)
            throw new ArgumentException("A hard reset throws work away, so it keeps a copy first: use HardReset.", nameof(mode));
        var (branch, current) = await BranchAtAsync(commit, ct).ConfigureAwait(false);
        await RunResetAsync(Options[mode], commit, ct).ConfigureAwait(false);
        return new ResetResult { Branch = branch, OldCommit = current, NewCommit = commit, Mode = mode };
    }

    /// <summary>The checked-out branch and its commit, when there is a branch to reset to <paramref name="commit"/>.</summary>
    public async Task<(string Branch, string Commit)> BranchAtAsync(string commit, CancellationToken ct)
    {
        FullId(commit);
        var head = await HeadState.ReadAsync(_reads, ct).ConfigureAwait(false);
        if (head is not { Branch: { } branch, Commit: { } current })
            throw new ChangeRefusedException("No branch is checked out here, so there is no branch to reset.");
        if (current == commit)
            throw new ChangeRefusedException($"{branch} is already at {ShortId.Of(commit)}.");
        return (branch, current);
    }

    /// <summary>Moves the branch back to where it was, the same way. Answers with that move, from new to old.</summary>
    public async Task<ResetResult> UndoAsync(ResetResult reset, CancellationToken ct)
    {
        if (reset.Mode == ResetMode.Hard)
            throw new ArgumentException("A hard reset is undone with HardReset, which brings the kept work back.", nameof(reset));
        await CheckStillAtAsync(reset, ct).ConfigureAwait(false);
        await RunResetAsync(Options[reset.Mode], reset.OldCommit, ct).ConfigureAwait(false);
        return reset with { OldCommit = reset.NewCommit, NewCommit = reset.OldCommit };
    }

    /// <summary>Refuses unless the branch is still where <paramref name="reset"/> left it.</summary>
    public async Task CheckStillAtAsync(ResetResult reset, CancellationToken ct)
    {
        FullId(reset.OldCommit);
        var head = await HeadState.ReadAsync(_reads, ct).ConfigureAwait(false);
        if (head.Branch != reset.Branch || head.Commit != reset.NewCommit)
            throw new ChangeRefusedException($"{reset.Branch} is no longer where the reset left it, so undo did nothing. Something has changed it since.");
    }

    /// <summary>
    /// Moves the branch and the files with <c>--hard</c>, which throws uncommitted work away, or with <c>--keep</c>,
    /// which stops instead of overwriting any. The caller must have kept the work first.
    /// </summary>
    public Task MoveFilesAsync(string commit, bool keepLocalChanges, CancellationToken ct)
    {
        FullId(commit);
        return RunResetAsync(keepLocalChanges ? "--keep" : "--hard", commit, ct);
    }

    /// <summary>A reset goes to a commit by its full id, never to a revision such as "HEAD~1".</summary>
    private static void FullId(string commit)
    {
        if (!ObjectId.IsValid(commit))
            throw new ArgumentException($"Not a full commit id: {commit}", nameof(commit));
    }

    private async Task RunResetAsync(string option, string commit, CancellationToken ct)
    {
        var result = await _retry.RunAsync(_changes, new GitCommandLine(["reset", option, commit], []), [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ChangeRefusedException("Git did not reset, so nothing changed. This is what git said:", result.StandardError);
    }
}
