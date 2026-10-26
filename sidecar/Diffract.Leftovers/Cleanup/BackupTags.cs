using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers.Rules;

namespace Diffract.Leftovers.Cleanup;

/// <summary>Work to keep before something is removed.</summary>
/// <param name="Kind">"worktree", "branch", "remote" or "stash", the tag's second part.</param>
/// <param name="Name">What the tag is named after, such as the branch.</param>
/// <param name="What">What the dialog and the tag's message call it, such as "branch claude/fix".</param>
internal sealed record BackupOf(string Kind, string Name, string Commit, string What, int CommitsAtRisk);

/// <summary>
/// Names and makes the <c>archive/</c> tags that keep work before it is removed: <c>archive/&lt;kind&gt;/&lt;name&gt;-&lt;date&gt;</c>,
/// as decision 1 says. A name already taken, by the repo or earlier in the same plan, gets "-2", "-3" and so on.
/// </summary>
internal sealed class BackupTags(RepoSnapshot repo, DateTimeOffset now)
{
    private readonly HashSet<string> _taken = repo.Tags.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    /// <param name="removal">The step the backup is for. The backup expects what it does, so no tag is made for a removal that would not run.</param>
    public CleanupStep Step(BackupOf backup, CleanupStep removal)
    {
        var tag = Unique($"archive/{backup.Kind}/{backup.Name}-{now:yyyy-MM-dd}");
        var commits = backup.CommitsAtRisk == 1 ? "1 commit" : $"{backup.CommitsAtRisk} commits";
        var message = $"Backup made by Diffract before removing {backup.What}, with {commits} that exist nowhere else.";
        return new CleanupStep
        {
            Kind = StepKind.Backup,
            Description = $"Keep a backup tag {tag} for {commits} that exist nowhere else",
            Command = new GitCommandLine(["tag", "-a", tag, backup.Commit, "-F", "-"], [message]),
            Expects = removal.Expects,
            Undo = new GitCommandLine(["tag", "-d", tag], []),
            UndoExpects = [new Expectation.TagOn($"refs/tags/{tag}", backup.Commit)],
        };
    }

    /// <summary>
    /// "Keep these": one tag on the orphaned commits, so git no longer deletes them in its next cleanup (decision 3).
    /// One commit is tagged as it is. Several get a commit made to hold them all, with no files of its own.
    /// Nothing is removed, so undo only takes the tag away again.
    /// </summary>
    public CleanupStep Keep(IReadOnlyList<UnreachableCommit> commits)
    {
        var newest = commits.MaxBy(c => c.CommittedAt)!;
        var tag = Unique($"archive/orphaned/{newest.Id[..12]}-{now:yyyy-MM-dd}");
        var them = commits.Count == 1 ? "1 commit" : $"{commits.Count} commits";
        var message = $"Kept by Diffract: {them} that {(commits.Count == 1 ? "was" : "were")} on no branch, tag, stash or reflog.";
        var step = new CleanupStep
        {
            Kind = StepKind.Backup,
            Description = $"Keep {them} under a backup tag {tag}",
            Expects = [.. commits.Select(c => new Expectation.ObjectExists(c.Id))],
            Undo = new GitCommandLine(["tag", "-d", tag], []),
            UndoNote = commits.Count == 1 ? "Undo takes the tag away, and git may then delete the commit again." : "Undo takes the tag away, and git may then delete the commits again.",
        };
        return commits.Count == 1
            ? step with { Command = new GitCommandLine(["tag", "-a", tag, newest.Id, "-F", "-"], [message]), UndoExpects = [new Expectation.TagOn($"refs/tags/{tag}", newest.Id)] }
            : step with { Kind = StepKind.KeepCommits, Command = Holder(commits), Keep = new KeepTag(tag, message), UndoExpects = [new Expectation.RefPresent($"refs/tags/{tag}")] };
    }

    /// <summary>A commit with every orphaned commit as a parent and git's empty tree, which every repo has.</summary>
    private static GitCommandLine Holder(IReadOnlyList<UnreachableCommit> commits) =>
        new(["commit-tree", EmptyTree, .. commits.SelectMany(c => new[] { "-p", c.Id }), "-F", "-"], [OrphanedCommitRule.HolderSubject]);

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private string Unique(string wanted)
    {
        var name = wanted;
        for (var n = 2; !_taken.Add(name); n++)
            name = $"{wanted}-{n}";
        return name;
    }
}
