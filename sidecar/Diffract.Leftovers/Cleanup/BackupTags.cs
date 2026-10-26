using Diffract.Git;
using Diffract.Git.Snapshot;

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

    private string Unique(string wanted)
    {
        var name = wanted;
        for (var n = 2; !_taken.Add(name); n++)
            name = $"{wanted}-{n}";
        return name;
    }
}
