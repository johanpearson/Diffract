using Diffract.Fixtures;
using Microsoft.Extensions.Time.Testing;
using static Diffract.Leftovers.LeftoverKind;
using static Diffract.Leftovers.Verdict;

namespace Diffract.Leftovers.Tests;

/// <summary>The exact leftovers and verdicts for every fixture scenario, with the default config.</summary>
public class ScenarioVerdictTests(ScenarioSnapshots snapshots)
{
    private const string Claude = "Claude Code";
    private const string Copilot = "GitHub Copilot";

    private async Task<IReadOnlyList<Leftover>> ClassifyAsync(string scenario) =>
        new LeftoverClassifier().Classify(
            await snapshots.GetAsync(scenario), DiffractConfig.Default, new FakeTimeProvider(FixtureRepoBuilder.DefaultNow));

    private async Task AssertLeftoversAsync(string scenario, params (LeftoverKind Kind, string Name, Verdict Verdict, int AtRisk, string? Agent)[] expected)
    {
        var actual = await ClassifyAsync(scenario);

        Assert.Equal(expected, actual.Select(l => (l.Kind, l.Name, l.Verdict, l.CommitsAtRisk, l.Agent)));
        Assert.All(actual, l => Assert.False(string.IsNullOrWhiteSpace(l.Reason)));
    }

    [Fact]
    public Task Clean() => AssertLeftoversAsync("Clean");

    [Fact]
    public Task MergedWorktree() => AssertLeftoversAsync("MergedWorktree", (Worktree, "feature/done", SafeToRemove, 0, null));

    [Fact]
    public Task PrunableWorktree() => AssertLeftoversAsync("PrunableWorktree", (LeftoverKind.PrunableWorktree, "feature/deleted", SafeToRemove, 0, null));

    [Fact]
    public Task StaleAgentWorktree() => AssertLeftoversAsync("StaleAgentWorktree", (Worktree, "claude/refactor-cart", NeedsReview, 2, Claude));

    [Fact]
    public Task ActiveAgentWorktree() => AssertLeftoversAsync("ActiveAgentWorktree", (Worktree, "claude/fix-flaky-login", InUse, 1, Claude));

    [Fact]
    public Task LockedWorktree() => AssertLeftoversAsync("LockedWorktree", (Worktree, "claude/long-task", InUse, 1, Claude));

    [Fact]
    public Task UserDirtyWorktree() => AssertLeftoversAsync("UserDirtyWorktree", (Worktree, "experiment", InUse, 0, null));

    [Fact]
    public Task AgentBranchMerged() => AssertLeftoversAsync("AgentBranchMerged", (LocalBranch, "copilot/update-deps", SafeToRemove, 0, Copilot));

    [Fact]
    public Task AgentBranchUnmerged() => AssertLeftoversAsync("AgentBranchUnmerged", (LocalBranch, "copilot/add-logging", NeedsReview, 3, Copilot));

    [Fact]
    public Task RemoteAgentBranch() => AssertLeftoversAsync("RemoteAgentBranch", (RemoteBranch, "origin/copilot/fix-readme", SafeToRemove, 0, Copilot));

    [Fact]
    public Task ToolStash() => AssertLeftoversAsync("ToolStash", (Stash, "stash@{0}", NeedsReview, 1, null));

    [Fact]
    public Task StaleCheckpointSession() =>
        AssertLeftoversAsync("StaleCheckpointSession", (CheckpointSession, "3f2b8c1e-7a4d-4e9b-9c61-2d5f0e8a7b13", NeedsReview, 0, Copilot));

    [Fact]
    public Task RecentCheckpointSession() => AssertLeftoversAsync("RecentCheckpointSession");

    [Fact]
    public Task SquashMergedBranch() => AssertLeftoversAsync("SquashMergedBranch", (LocalBranch, "feature/search", SafeToRemove, 2, null));

    [Fact]
    public Task PrewarmWorktree() => AssertLeftoversAsync("PrewarmWorktree");

    [Fact]
    public Task LeftoverWorktreeFolder() => AssertLeftoversAsync("LeftoverWorktreeFolder", (LeftoverFolder, "dev-expert-robot", NeedsReview, 0, Copilot));

    [Fact]
    public async Task OrphanedCommit()
    {
        var snapshot = await snapshots.GetAsync("OrphanedCommit");

        await AssertLeftoversAsync("OrphanedCommit", (LeftoverKind.OrphanedCommit, snapshot.UnreachableCommits[0][..12], NeedsReview, 1, null));
    }

    [Fact]
    public Task OldArchiveTag() => AssertLeftoversAsync("OldArchiveTag", (BackupTag, "archive/old-idea", NeedsReview, 1, null));

    [Fact]
    public Task RecentArchiveTag() => AssertLeftoversAsync("RecentArchiveTag");

    [Fact]
    public Task AwkwardPaths() => AssertLeftoversAsync("AwkwardPaths",
        (Worktree, "feature/long", SafeToRemove, 0, null),
        (Worktree, "feature/smörgås", SafeToRemove, 0, null),
        (Worktree, "feature/spaces", SafeToRemove, 0, null));

    [Fact]
    public Task DetachedWorktree() => AssertLeftoversAsync("DetachedWorktree",
        (Worktree, "detached-clean", NeedsReview, 0, null),
        (Worktree, "detached-dirty", InUse, 0, null));

    [Fact]
    public async Task MasterAsMain()
    {
        await AssertLeftoversAsync("MasterAsMain", (LocalBranch, "copilot/fix-typo", SafeToRemove, 0, Copilot));

        Assert.Equal("Merged into master", Assert.Single(await ClassifyAsync("MasterAsMain")).Reason);
    }

    [Fact]
    public async Task Reasons_read_as_plain_sentences()
    {
        Assert.Equal("No activity for 9 days, 2 commits exist nowhere else", Assert.Single(await ClassifyAsync("StaleAgentWorktree")).Reason);
        Assert.Equal("Files changed 1 minute ago", Assert.Single(await ClassifyAsync("ActiveAgentWorktree")).Reason);
        Assert.Equal("3 checkpoints, newest is 9 days old", Assert.Single(await ClassifyAsync("StaleCheckpointSession")).Reason);
    }

    [Fact]
    public void Every_scenario_has_a_test()
    {
        var tested = typeof(ScenarioVerdictTests).GetMethods().Select(m => m.Name).ToHashSet();

        Assert.All(FixtureRepoBuilder.ScenarioNames, name => Assert.Contains(name, tested));
    }
}
