using Diffract.Fixtures;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Fixtures;

/// <summary>
/// Each scenario produces the snapshot facts its rule will read. The verdicts themselves are tested with the rules.
/// </summary>
public class FixtureRepoBuilderTests
{
    private static readonly DateTimeOffset Now = FixtureRepoBuilder.DefaultNow;

    private static readonly SnapshotOptions Options = new()
    {
        WorktreeContainerPatterns = ["**/copilot-worktrees/*", "**/.claude/worktrees"],
        AnalyzeRisk = _ => true,
    };

    private static async Task<(FixtureScenario Scenario, RepoSnapshot Snapshot)> BuildAsync(TempDirectory temp, string name, DateTimeOffset? now = null)
    {
        var scenario = await new FixtureRepoBuilder(temp, now ?? Now).BuildAsync(name);
        var result = await new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(temp) }, Options)
            .BuildAsync(scenario.RepoPath, TestContext.Current.CancellationToken);
        Assert.Equal(name, scenario.Name);
        Assert.Equal(Full(temp.Combine(name, "repo")), Full(scenario.RepoPath));
        return (scenario, result.Snapshot);
    }

    private static async Task<RepoSnapshot> SnapshotAsync(TempDirectory temp, string name) => (await BuildAsync(temp, name)).Snapshot;

    private static WorktreeInfo Linked(RepoSnapshot snapshot) => Assert.Single(snapshot.Worktrees, w => !w.IsMain);

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // --- the builder ------------------------------------------------------------

    [Fact]
    public void Has_every_scenario_from_the_milestone()
    {
        Assert.Equal(
            [
                "Clean", "MergedWorktree", "PrunableWorktree", "StaleAgentWorktree", "ActiveAgentWorktree", "LockedWorktree",
                "UserDirtyWorktree", "AgentBranchMerged", "AgentBranchUnmerged", "RemoteAgentBranch", "ToolStash",
                "StaleCheckpointSession", "RecentCheckpointSession", "SquashMergedBranch", "PrewarmWorktree",
                "LeftoverWorktreeFolder", "OrphanedCommit", "OldArchiveTag", "RecentArchiveTag", "AwkwardPaths",
                "DetachedWorktree", "MasterAsMain",
            ],
            FixtureRepoBuilder.ScenarioNames);
    }

    [Fact]
    public async Task Unknown_scenario_is_rejected()
    {
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new FixtureRepoBuilder(temp).BuildAsync("NoSuchThing"));

        Assert.Contains("Clean", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_ignore_case()
    {
        using var temp = new TempDirectory();

        var scenario = await new FixtureRepoBuilder(temp).BuildAsync("clean");

        Assert.Equal("Clean", scenario.Name);
    }

    [Fact]
    public async Task Dates_follow_the_builders_now()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2027, 6, 1, 8, 0, 0, TimeSpan.Zero);

        var (_, snapshot) = await BuildAsync(temp, "StaleAgentWorktree", now);

        Assert.Equal(now.AddDays(-9), Linked(snapshot).LastActivityAt);
    }

    [Fact]
    public async Task Scenarios_can_share_one_folder()
    {
        using var temp = new TempDirectory();
        var builder = new FixtureRepoBuilder(temp);

        var first = await builder.BuildAsync("Clean");
        var second = await builder.BuildAsync("ToolStash");

        Assert.NotEqual(first.RepoPath, second.RepoPath);
        Assert.Empty((await SnapshotAsync(temp, "OrphanedCommit")).Stashes);
    }

    // --- fixture output folder --------------------------------------------------

    [Fact]
    public void Output_folder_is_created_and_kept()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("out");

        using (TempDirectory.ForFixtureOutput(path))
        {
        }

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Output_folder_must_be_empty()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("existing.txt"), "x");

        Assert.Throws<InvalidOperationException>(() => TempDirectory.ForFixtureOutput(temp.Path));
    }

    [Fact]
    public void Output_folder_path_must_be_short_enough_for_git()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine(new string('x', TempDirectory.MaxFixtureOutputLength));

        Assert.Throws<InvalidOperationException>(() => TempDirectory.ForFixtureOutput(path));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task Output_folder_must_not_be_inside_a_repo()
    {
        using var temp = new TempDirectory();
        var repo = await FixtureRepo.InitAsync(temp, "real");

        var error = Assert.Throws<InvalidOperationException>(() => TempDirectory.ForFixtureOutput(Path.Combine(repo.Path, "sub", "out")));

        Assert.Contains(repo.Path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(repo.Path, "sub")));
    }

    // --- scenarios --------------------------------------------------------------

    [Fact]
    public async Task Clean()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "Clean");

        var main = Assert.Single(snapshot.Worktrees);
        Assert.Equal(0, main.DirtyFileCount);
        Assert.Equal("main", Assert.Single(snapshot.Branches).Name);
        Assert.Empty(snapshot.Tags);
        Assert.Empty(snapshot.Stashes);
        Assert.Empty(snapshot.OtherRefs);
        Assert.Empty(snapshot.UnreachableCommits);
        Assert.Empty(snapshot.LeftoverFolders);
    }

    [Fact]
    public async Task MergedWorktree()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "MergedWorktree");

        var worktree = Linked(snapshot);
        Assert.Equal(("feature/done", 0, Now.AddDays(-4)), (worktree.Branch, worktree.DirtyFileCount, worktree.LastActivityAt));
        var branch = snapshot.Branches.Single(b => b.Name == "feature/done");
        Assert.Equal((true, 0), (branch.IsMergedIntoMain, branch.CommitsAtRisk));
    }

    [Fact]
    public async Task PrunableWorktree()
    {
        using var temp = new TempDirectory();

        var worktree = Linked(await SnapshotAsync(temp, "PrunableWorktree"));

        Assert.True(worktree.IsPrunable);
        Assert.Equal("feature/deleted", worktree.Branch);
    }

    [Fact]
    public async Task StaleAgentWorktree()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "StaleAgentWorktree");

        var worktree = Linked(snapshot);
        Assert.Equal(("claude/refactor-cart", 0, 2), (worktree.Branch, worktree.DirtyFileCount, worktree.AheadOfMain));
        Assert.Equal(Now.AddDays(-9), worktree.LastActivityAt);
        Assert.EndsWith(Path.Combine(".claude", "worktrees", "refactor-cart"), worktree.Path, StringComparison.Ordinal);
        var branch = snapshot.Branches.Single(b => b.Name == "claude/refactor-cart");
        Assert.Equal((false, 2), (branch.IsMergedIntoMain, branch.CommitsAtRisk));
        Assert.Empty(snapshot.LeftoverFolders);
    }

    [Fact]
    public async Task ActiveAgentWorktree()
    {
        using var temp = new TempDirectory();

        var worktree = Linked(await SnapshotAsync(temp, "ActiveAgentWorktree"));

        Assert.Equal(("claude/fix-flaky-login", 1), (worktree.Branch, worktree.DirtyFileCount));
        Assert.Equal(Now.AddMinutes(-1), worktree.LastActivityAt);
    }

    [Fact]
    public async Task LockedWorktree()
    {
        using var temp = new TempDirectory();

        var worktree = Linked(await SnapshotAsync(temp, "LockedWorktree"));

        Assert.Equal((true, "Claude Code agent is running"), (worktree.IsLocked, worktree.LockReason));
        Assert.Equal((0, 1), (worktree.DirtyFileCount, worktree.AheadOfMain));
        Assert.Equal(Now.AddDays(-20), worktree.LastActivityAt);
    }

    [Fact]
    public async Task UserDirtyWorktree()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "UserDirtyWorktree");

        var worktree = Linked(snapshot);
        Assert.Equal(("experiment", 1, 0), (worktree.Branch, worktree.DirtyFileCount, worktree.AheadOfMain));
        Assert.Equal(Now.AddDays(-20), worktree.LastActivityAt);
        Assert.True(snapshot.Branches.Single(b => b.Name == "experiment").IsMergedIntoMain);
    }

    [Fact]
    public async Task AgentBranchMerged()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "AgentBranchMerged");

        Assert.Single(snapshot.Worktrees);
        var branch = snapshot.Branches.Single(b => b.Name == "copilot/update-deps");
        Assert.Equal((true, 0), (branch.IsMergedIntoMain, branch.CommitsAtRisk));
    }

    [Fact]
    public async Task AgentBranchUnmerged()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "AgentBranchUnmerged");

        Assert.Single(snapshot.Worktrees);
        var branch = snapshot.Branches.Single(b => b.Name == "copilot/add-logging");
        Assert.Equal((false, 3, Now.AddDays(-9)), (branch.IsMergedIntoMain, branch.CommitsAtRisk, branch.LastCommitAt));
    }

    [Fact]
    public async Task RemoteAgentBranch()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "RemoteAgentBranch");

        Assert.Equal("main", snapshot.MainBranch);
        Assert.Equal(["main", "origin/copilot/fix-readme", "origin/main"], snapshot.Branches.Select(b => b.Name).Order(StringComparer.Ordinal));
        var remote = snapshot.Branches.Single(b => b.Name == "origin/copilot/fix-readme");
        Assert.Equal((true, true, 0), (remote.IsRemote, remote.IsMergedIntoMain, remote.CommitsAtRisk));
    }

    [Fact]
    public async Task ToolStash()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "ToolStash");

        var stash = Assert.Single(snapshot.Stashes);
        Assert.Equal(("On main: autostash", Now.AddDays(-3)), (stash.Message, stash.CreatedAt));
        Assert.Equal(0, Assert.Single(snapshot.Worktrees).DirtyFileCount);
    }

    [Fact]
    public async Task StaleCheckpointSession()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "StaleCheckpointSession");

        AssertCheckpointSession(snapshot, "3f2b8c1e-7a4d-4e9b-9c61-2d5f0e8a7b13", Now.AddDays(-9));
    }

    [Fact]
    public async Task RecentCheckpointSession()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "RecentCheckpointSession");

        AssertCheckpointSession(snapshot, "9a4e2d71-5c3b-4f8a-b2e6-71d0c9f4a385", Now.AddDays(-2));
    }

    private static void AssertCheckpointSession(RepoSnapshot snapshot, string session, DateTimeOffset newest)
    {
        Assert.Equal(3, snapshot.OtherRefs.Count);
        Assert.All(snapshot.OtherRefs, r => Assert.Matches($@"^refs/copilot/checkpoints/{session}/\d{{20}}/[0-9a-f-]{{36}}$", r.FullName));
        Assert.Equal(3, snapshot.OtherRefs.Select(r => r.TargetId).Distinct().Count());
        Assert.Equal(newest, snapshot.OtherRefs.Max(r => r.CreatedAt));
        Assert.Empty(snapshot.UnreachableCommits);
    }

    [Fact]
    public async Task SquashMergedBranch()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "SquashMergedBranch");

        var branch = snapshot.Branches.Single(b => b.Name == "feature/search");
        Assert.Equal(("origin/feature/search", true, false, 2), (branch.Upstream, branch.IsUpstreamGone, branch.IsMergedIntoMain, branch.CommitsAtRisk));
        Assert.DoesNotContain(snapshot.Branches, b => b.Name == "origin/feature/search");
    }

    [Fact]
    public async Task PrewarmWorktree()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "PrewarmWorktree");

        var worktree = Linked(snapshot);
        Assert.Equal(Full(temp.Combine("PrewarmWorktree", "copilot-worktrees", "repo", "copilot-prewarm-curly-waddle")), worktree.Path);
        Assert.Equal((true, 0, 0), (worktree.IsDetached, worktree.DirtyFileCount, worktree.AheadOfMain));
        Assert.Equal(Now.AddDays(-9), worktree.LastActivityAt);
        Assert.Empty(snapshot.LeftoverFolders);
    }

    [Fact]
    public async Task LeftoverWorktreeFolder()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "LeftoverWorktreeFolder");

        Assert.Equal([Full(temp.Combine("LeftoverWorktreeFolder", "copilot-worktrees", "repo", "dev-expert-robot"))], snapshot.LeftoverFolders);
        Assert.True(Linked(snapshot).IsDetached);
    }

    [Fact]
    public async Task OrphanedCommit()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "OrphanedCommit");

        Assert.Single(snapshot.UnreachableCommits);
        Assert.Single(snapshot.Branches);
    }

    [Fact]
    public async Task OldArchiveTag()
    {
        using var temp = new TempDirectory();

        var tag = Assert.Single((await SnapshotAsync(temp, "OldArchiveTag")).Tags);

        Assert.Equal(("archive/old-idea", Now.AddDays(-45), false, 1), (tag.Name, tag.CreatedAt, tag.IsMergedIntoMain, tag.CommitsAtRisk));
    }

    [Fact]
    public async Task RecentArchiveTag()
    {
        using var temp = new TempDirectory();

        var tag = Assert.Single((await SnapshotAsync(temp, "RecentArchiveTag")).Tags);

        Assert.Equal(("archive/new-idea", Now.AddDays(-5), false, 1), (tag.Name, tag.CreatedAt, tag.IsMergedIntoMain, tag.CommitsAtRisk));
    }

    [Fact]
    public async Task AwkwardPaths()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "AwkwardPaths");

        var linked = snapshot.Worktrees.Where(w => !w.IsMain).ToList();
        Assert.Equal(["feature/long", "feature/smörgås", "feature/spaces"], linked.Select(w => w.Branch).Order(StringComparer.Ordinal));
        Assert.Contains(linked, w => w.Path == Full(temp.Combine("AwkwardPaths", "my worktrees", "feature with spaces")));
        Assert.Contains(linked, w => w.Path == Full(temp.Combine("AwkwardPaths", "räksmörgås", "åäö ÅÄÖ")));
        Assert.Contains(linked, w => w.Path.Length >= 200);
        Assert.All(linked, w => Assert.Equal((0, 0, Now.AddDays(-3)), (w.DirtyFileCount, w.AheadOfMain, w.LastActivityAt)));
        Assert.All(linked, w => Assert.True(Directory.Exists(w.Path)));
        Assert.Contains(snapshot.Branches, b => b.Name == "feature/smörgås" && b.IsMergedIntoMain);
    }

    [Fact]
    public async Task DetachedWorktree()
    {
        using var temp = new TempDirectory();

        var linked = (await SnapshotAsync(temp, "DetachedWorktree")).Worktrees.Where(w => !w.IsMain).OrderBy(w => w.Path, StringComparer.Ordinal).ToList();

        Assert.Equal(2, linked.Count);
        Assert.All(linked, w => Assert.Equal((true, (string?)null, Now.AddDays(-9)), (w.IsDetached, w.Branch, w.LastActivityAt)));
        Assert.Equal([0, 1], linked.Select(w => w.DirtyFileCount));
    }

    [Fact]
    public async Task MasterAsMain()
    {
        using var temp = new TempDirectory();

        var snapshot = await SnapshotAsync(temp, "MasterAsMain");

        Assert.Equal("master", snapshot.MainBranch);
        Assert.Equal(["copilot/fix-typo", "master"], snapshot.Branches.Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.True(snapshot.Branches.Single(b => b.Name == "copilot/fix-typo").IsMergedIntoMain);
    }
}
