using Diffract.Fixtures;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Snapshot;

/// <summary>
/// One test per row of the phase 3 table: make the change in a fixture repo, refresh only that part, then check
/// which git calls ran and that the result matches a full rebuild made right after.
/// </summary>
public sealed class PartialRefreshTests : IDisposable
{
    private static readonly SnapshotOptions Options = new()
    {
        WorktreeContainerPatterns = ["**/copilot-worktrees/*", "**/.claude/worktrees"],
        IncludeUnreachable = false,
    };

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RepoSnapshotBuilder Builder() => new(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }, Options);

    /// <summary>A main worktree and an agent worktree on claude/refactor-cart, under .claude/worktrees.</summary>
    private async Task<(string Repo, string Worktree, RepoSnapshot Before)> AgentRepoAsync()
    {
        var repo = (await new FixtureRepoBuilder(_temp).StaleAgentWorktreeAsync()).RepoPath;
        var worktree = Path.GetFullPath(Path.Combine(repo, ".claude", "worktrees", "refactor-cart"));
        return (repo, worktree, (await Builder().BuildAsync(repo, Ct)).Snapshot);
    }

    private Task<GitResult> GitAsync(string workingDirectory, params string[] args) =>
        IsolatedGit.WritableRunner(_temp, workingDirectory).RunAsync(args, Ct);

    private async Task<(RepoSnapshot Partial, string[] Calls, RepoSnapshot Full)> RefreshAsync(RepoSnapshot before, ChangeSet changes)
    {
        var result = await Builder().RefreshAsync(before, changes, Ct);
        var full = (await Builder().BuildAsync(before.RepoRoot, Ct)).Snapshot;
        return (result.Snapshot, [.. result.Timings.Select(t => Subcommand(t.Command)).Order(StringComparer.Ordinal)], full);
    }

    // "git rev-list --left-right ..." becomes "rev-list"; "git worktree list ..." becomes "worktree list".
    private static string Subcommand(string command)
    {
        var parts = command.Split(' ');
        return parts[1] is "worktree" or "stash" or "config" ? $"{parts[1]} {parts[2]}" : parts[1];
    }

    private static void AssertSameWorktrees(RepoSnapshot expected, RepoSnapshot actual)
    {
        Assert.Equal(expected.Worktrees, actual.Worktrees);
        Assert.Equal(expected.LeftoverFolders, actual.LeftoverFolders);
    }

    private static void AssertSameAsFull(RepoSnapshot expected, RepoSnapshot actual)
    {
        AssertSameWorktrees(expected, actual);
        Assert.Equal(expected.MainBranch, actual.MainBranch);
        Assert.Equal(expected.Branches, actual.Branches);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.Stashes, actual.Stashes);
        Assert.Equal(expected.OtherRefs, actual.OtherRefs);
    }

    [Fact]
    public async Task Files_in_one_worktree_cost_one_status()
    {
        var (_, worktree, before) = await AgentRepoAsync();
        File.WriteAllText(Path.Combine(worktree, "notes.txt"), "todo");

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.FilesChanged(worktree));

        Assert.Equal(["status"], calls);
        Assert.Equal(1, partial.Worktrees.Single(w => w.Path == worktree).DirtyFileCount);
        AssertSameAsFull(full, partial);
    }

    [Fact]
    public async Task A_staged_change_costs_status_and_ahead_behind()
    {
        var (_, worktree, before) = await AgentRepoAsync();
        File.WriteAllText(Path.Combine(worktree, "cart.txt"), "v3");
        await GitAsync(worktree, "add", "cart.txt");

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.HeadChanged(worktree));

        Assert.Equal(["rev-list", "status"], calls);
        AssertSameAsFull(full, partial);
    }

    [Fact]
    public async Task Detaching_at_the_same_commit_costs_two_calls()
    {
        var (_, worktree, before) = await AgentRepoAsync();
        await GitAsync(worktree, "switch", "--detach");

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.HeadChanged(worktree));

        Assert.Equal(["rev-list", "status"], calls);
        var refreshed = partial.Worktrees.Single(w => w.Path == worktree);
        Assert.Equal((true, null), (refreshed.IsDetached, refreshed.Branch));
        AssertSameAsFull(full, partial);
    }

    [Fact]
    public async Task Head_moving_to_a_commit_no_branch_points_at_costs_one_more_call_for_its_date()
    {
        var (_, worktree, before) = await AgentRepoAsync();
        await GitAsync(worktree, "switch", "--detach", "HEAD~1");

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.HeadChanged(worktree));

        Assert.Equal(["log", "rev-list", "status"], calls);
        Assert.Equal(1, partial.Worktrees.Single(w => w.Path == worktree).AheadOfMain);
        AssertSameAsFull(full, partial);
    }

    [Fact]
    public async Task A_new_ref_refreshes_refs_and_every_worktree_without_status_or_fsck()
    {
        var (repo, _, before) = await AgentRepoAsync();
        await GitAsync(repo, "branch", "copilot/new-idea");

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.RefsChanged);

        Assert.DoesNotContain("status", calls);
        Assert.DoesNotContain("worktree list", calls);
        Assert.DoesNotContain("fsck", calls);
        Assert.DoesNotContain("version", calls);
        // One ahead/behind per worktree, one commits-at-risk count per unmerged local branch or tag.
        Assert.Equal(
            partial.Worktrees.Count + partial.Branches.Count(b => !b.IsRemote && !b.IsMergedIntoMain) + partial.Tags.Count(t => !t.IsMergedIntoMain),
            calls.Count(c => c == "rev-list"));
        Assert.Contains(partial.Branches, b => b.Name == "copilot/new-idea");
        AssertSameAsFull(full, partial);
    }

    [Fact]
    public async Task A_commit_in_a_worktree_moves_its_head_through_the_refs_refresh()
    {
        var (_, worktree, before) = await AgentRepoAsync();
        File.WriteAllText(Path.Combine(worktree, "cart.txt"), "v3");
        await GitAsync(worktree, "commit", "-q", "-am", "Refactor cart again");

        // What the watcher reports for a commit: the branch ref and the worktree's index.
        var (partial, _, full) = await RefreshAsync(before, ChangeSet.RefsChanged.Merge(ChangeSet.HeadChanged(worktree)));

        Assert.Equal(3, partial.Worktrees.Single(w => w.Path == worktree).AheadOfMain);
        AssertSameAsFull(full, partial);
    }

    [Fact]
    public async Task A_new_worktree_costs_the_list_and_two_calls_for_the_new_one()
    {
        var (repo, _, before) = await AgentRepoAsync();
        var added = Path.GetFullPath(Path.Combine(repo, ".claude", "worktrees", "fix-login"));
        await GitAsync(repo, "worktree", "add", "-q", "--detach", added);

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.WorktreeListChanged);

        Assert.Equal(["rev-list", "status", "worktree list"], calls);
        Assert.Contains(partial.Worktrees, w => w.Path == added);
        AssertSameWorktrees(full, partial);
    }

    [Fact]
    public async Task A_removed_worktree_costs_only_the_list()
    {
        var (repo, worktree, before) = await AgentRepoAsync();
        await GitAsync(repo, "worktree", "remove", "--force", worktree);

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.WorktreeListChanged);

        Assert.Equal(["worktree list"], calls);
        Assert.Single(partial.Worktrees);
        AssertSameWorktrees(full, partial);
    }

    [Fact]
    public async Task Locking_a_worktree_costs_only_the_list()
    {
        var (repo, worktree, before) = await AgentRepoAsync();
        await GitAsync(repo, "worktree", "lock", "--reason", "agent at work", worktree);

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.WorktreeListChanged);

        Assert.Equal(["worktree list"], calls);
        Assert.Equal((true, "agent at work"), (partial.Worktrees[1].IsLocked, partial.Worktrees[1].LockReason));
        AssertSameWorktrees(full, partial);
    }

    [Fact]
    public async Task A_leftover_folder_shows_up_with_the_worktree_list()
    {
        var (repo, _, before) = await AgentRepoAsync();
        var leftover = Path.GetFullPath(Path.Combine(repo, ".claude", "worktrees", "gone-task"));
        Directory.CreateDirectory(leftover);

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.WorktreeListChanged);

        Assert.Equal(["worktree list"], calls);
        Assert.Contains(partial.LeftoverFolders, f => f.Path == leftover && f.HoldsNoFiles);
        AssertSameWorktrees(full, partial);
    }

    [Fact]
    public async Task Everything_is_a_full_snapshot_without_fsck_that_keeps_the_unreachable_commits()
    {
        var (repo, _, _) = await AgentRepoAsync();
        var withFsck = new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }, Options with { IncludeUnreachable = true });
        var before = (await withFsck.BuildAsync(repo, Ct)).Snapshot with
        {
            UnreachableCommits = [new UnreachableCommit("0123456789abcdef0123456789abcdef01234567", IsolatedGit.DefaultDate, "Kept")],
        };

        var (partial, calls, full) = await RefreshAsync(before, ChangeSet.All);

        Assert.DoesNotContain("fsck", calls);
        Assert.Contains("worktree list", calls);
        Assert.Equal("Kept", Assert.Single(partial.UnreachableCommits).Subject);
        AssertSameAsFull(full, partial);
    }

    [Fact]
    public async Task Nothing_changed_costs_nothing()
    {
        var (_, _, before) = await AgentRepoAsync();

        var (partial, calls, _) = await RefreshAsync(before, ChangeSet.None);

        Assert.Empty(calls);
        Assert.Equal(before.Worktrees, partial.Worktrees);
    }
}
