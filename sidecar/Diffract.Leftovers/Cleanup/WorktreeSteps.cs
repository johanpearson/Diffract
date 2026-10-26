using Diffract.Git;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Cleanup;

/// <summary>The steps for a worktree, a worktree whose folder is gone, and a leftover folder.</summary>
internal static class WorktreeSteps
{
    /// <summary>
    /// A backup when it holds work that exists nowhere else, then the worktree, then its branch as a step of its own,
    /// which the dialog lets the user leave out (decision 6). Only a worktree no program has a file open in is
    /// removed: git can otherwise remove part of it and stop.
    /// </summary>
    public static IReadOnlyList<CleanupStep> Worktree(WorktreeInfo worktree, int? commitsAtRisk, BackupTags backups) =>
    [
        .. Backup(worktree, commitsAtRisk, backups),
        new CleanupStep
        {
            Kind = StepKind.RemoveWorktree,
            Description = $"Remove worktree {worktree.Path}",
            Command = Remove(worktree),
            Expects = [new Expectation.NoFilesInUse(worktree.Path), new Expectation.WorktreeClean(worktree.Path), .. BranchAt(worktree)],
            Undo = Readd(worktree),
        },
        .. BranchStep(worktree),
    ];

    /// <summary>
    /// Its folder is gone, so only git's record of it goes, with <c>worktree remove</c>: <c>worktree prune</c> would
    /// take every such worktree, not this one. Undo can bring back the branch, not the folder.
    /// </summary>
    public static IReadOnlyList<CleanupStep> Prunable(WorktreeInfo worktree, int? commitsAtRisk, BackupTags backups) =>
    [
        .. Backup(worktree, commitsAtRisk, backups),
        new CleanupStep
        {
            Kind = StepKind.RemoveWorktree,
            Description = $"Forget worktree {worktree.Path}, whose folder is gone",
            Command = Remove(worktree),
            Expects = [.. BranchAt(worktree)],
            UndoNote = "The folder was already gone, so undo brings back the branch, not the folder.",
        },
        .. BranchStep(worktree),
    ];

    /// <summary>Windows itself is the undo: the folder can be restored from the Recycle Bin.</summary>
    public static IReadOnlyList<CleanupStep> Folder(string path) =>
    [
        new CleanupStep
        {
            Kind = StepKind.RecycleFolder,
            Description = $"Move folder {path} to the Recycle Bin",
            Expects = [new Expectation.NoFilesInUse(path)],
            UndoNote = "Restore it from the Recycle Bin.",
            Folder = path,
        },
    ];

    private static IEnumerable<CleanupStep> Backup(WorktreeInfo worktree, int? commitsAtRisk, BackupTags backups) =>
        commitsAtRisk is > 0 and var count && worktree.Head is { } head
            ? [backups.Step(new BackupOf("worktree", worktree.Branch ?? Path.GetFileName(worktree.Path), head, $"worktree {worktree.Path}", count))]
            : [];

    private static GitCommandLine Remove(WorktreeInfo worktree) => new(["worktree", "remove", worktree.Path], []);

    private static IEnumerable<Expectation> BranchAt(WorktreeInfo worktree) =>
        worktree is { Branch: { } branch, Head: { } head } ? [new Expectation.RefAt($"refs/heads/{branch}", head)] : [];

    /// <summary>The branch comes back first, in reverse order, so the worktree can be added on it again.</summary>
    private static GitCommandLine? Readd(WorktreeInfo worktree) => (worktree.Branch ?? worktree.Head) is { } at
        ? new GitCommandLine(["worktree", "add", worktree.Path, at], [])
        : null;

    private static IEnumerable<CleanupStep> BranchStep(WorktreeInfo worktree) => worktree is { Branch: { } branch, Head: { } head }
        ?
        [
            new CleanupStep
            {
                Kind = StepKind.DeleteBranch,
                Description = $"Delete branch {branch}",
                Command = new GitCommandLine(["branch", "-D", branch], []),
                Expects = [new Expectation.RefAt($"refs/heads/{branch}", head)],
                Undo = new GitCommandLine(["branch", branch, head], []),
                Optional = true,
            },
        ]
        : [];
}
