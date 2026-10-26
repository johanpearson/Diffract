using Diffract.Git;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Cleanup;

/// <summary>The steps for branches, remote-tracking branches, stashes, checkpoint sessions and old backup tags.</summary>
internal static class RefSteps
{
    /// <summary>Undo works for as long as git keeps the commits, which it does for a while after nothing points at them.</summary>
    private const string WhileGitKeepsThem = "Undo works while git still has the commits.";

    /// <summary>
    /// A backup first when it holds work that exists nowhere else, except when its upstream is gone: such a branch was
    /// most likely squash-merged, and would otherwise leave a tag for every one (decision 8).
    /// </summary>
    public static IReadOnlyList<CleanupStep> LocalBranch(BranchInfo branch, BackupTags backups)
    {
        var atRisk = branch.CommitsAtRisk ?? 0;
        var backup = atRisk > 0 && !branch.IsUpstreamGone;
        return
        [
            .. backup ? [backups.Step(new BackupOf("branch", branch.Name, branch.TargetId, $"branch {branch.Name}", atRisk))] : Array.Empty<CleanupStep>(),
            new CleanupStep
            {
                Kind = StepKind.DeleteBranch,
                Description = $"Delete branch {branch.Name}",
                Command = new GitCommandLine(["branch", "-D", branch.Name], []),
                Expects = [new Expectation.RefAt(branch.FullName, branch.TargetId)],
                Undo = new GitCommandLine(["branch", branch.Name, branch.TargetId], []),
                UndoNote = atRisk > 0 && !backup ? WhileGitKeepsThem : null,
            },
        ];
    }

    /// <summary>Only Diffract's copy of it goes. The branch on the remote is never touched.</summary>
    public static IReadOnlyList<CleanupStep> RemoteBranch(BranchInfo branch, BackupTags backups)
    {
        var atRisk = branch.CommitsAtRisk ?? 0;
        return
        [
            .. atRisk > 0 ? [backups.Step(new BackupOf("remote", branch.Name, branch.TargetId, $"remote-tracking branch {branch.Name}", atRisk))] : Array.Empty<CleanupStep>(),
            new CleanupStep
            {
                Kind = StepKind.DeleteRemoteBranch,
                Description = $"Delete remote-tracking branch {branch.Name}, here only",
                Command = new GitCommandLine(["branch", "-d", "-r", branch.Name], []),
                Expects = [new Expectation.RefAt(branch.FullName, branch.TargetId)],
                Undo = Create((branch.FullName, branch.TargetId)),
            },
        ];
    }

    /// <summary>Always backed up: a stash's work exists nowhere else. Undo puts it back on top of the list.</summary>
    public static IReadOnlyList<CleanupStep> Stash(StashInfo stash, BackupTags backups) =>
    [
        backups.Step(new BackupOf("stash", stash.CommitId[..12], stash.CommitId, $"stash \"{stash.Message}\"", 1)),
        new CleanupStep
        {
            Kind = StepKind.DropStash,
            Description = $"Drop stash \"{stash.Message}\"",
            Command = new GitCommandLine(["stash", "drop", stash.Name], []),
            Expects = [new Expectation.StashPresent(stash.CommitId)],
            Undo = new GitCommandLine(["stash", "store", "-m", stash.Message, stash.CommitId], []),
            UndoNote = "Undo puts it back on top of the list.",
        },
    ];

    /// <summary>All of a session's refs in one transaction, each from the value it had. No backup (decision 4).</summary>
    public static IReadOnlyList<CleanupStep> CheckpointSession(string session, IReadOnlyList<RefInfo> refs) =>
    [
        new CleanupStep
        {
            Kind = StepKind.DeleteRefs,
            Description = $"Delete {(refs.Count == 1 ? "1 checkpoint" : $"{refs.Count} checkpoints")} of session {session}",
            Command = Transaction(refs.Select(r => $"delete {r.FullName} {r.TargetId}")),
            Expects = [.. refs.Select(r => new Expectation.RefAt(r.FullName, r.TargetId))],
            Undo = Create([.. refs.Select(r => (r.FullName, r.TargetId))]),
            UndoNote = WhileGitKeepsThem,
        },
    ];

    /// <summary>It is the backup, so nothing is kept. Undo puts back the tag itself, its message included.</summary>
    public static IReadOnlyList<CleanupStep> OldBackupTag(TagInfo tag)
    {
        var holds = tag.ObjectId ?? tag.TargetId;
        return
        [
            new CleanupStep
            {
                Kind = StepKind.DeleteTag,
                Description = $"Delete old backup tag {tag.Name}",
                Command = new GitCommandLine(["tag", "-d", tag.Name], []),
                Expects = [new Expectation.RefAt(tag.FullName, holds)],
                Undo = Create((tag.FullName, holds)),
                UndoNote = WhileGitKeepsThem,
            },
        ];
    }

    private static GitCommandLine Create(params (string Ref, string Id)[] refs) =>
        Transaction(refs.Select(r => $"create {r.Ref} {r.Id}"));

    /// <summary>All or nothing: one ref that moved stops them all.</summary>
    private static GitCommandLine Transaction(IEnumerable<string> lines) =>
        new(["update-ref", "--stdin"], ["start", .. lines, "prepare", "commit"]);
}
