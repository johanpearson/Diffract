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

    private async Task AssertLeftoversAsync(string scenario, params (LeftoverKind Kind, string Name, Verdict Verdict, int? AtRisk, string? Agent)[] expected)
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
    public Task LeftoverWorktreeFolder() => AssertLeftoversAsync("LeftoverWorktreeFolder", (LeftoverFolder, "dev-expert-robot", NeedsReview, null, Copilot));

    [Fact]
    public Task OrphanedCommit() => AssertLeftoversAsync("OrphanedCommit",
        (LeftoverKind.OrphanedCommit, "2 commits of session d2061d20", NeedsReview, 2, Copilot),
        // A commit of its own, named by its id, which the fixture's fixed dates keep the same on every run.
        (LeftoverKind.OrphanedCommit, "49919a09 Experiment", NeedsReview, 1, null));

    [Fact]
    public Task ReflogOnlyCommit() => AssertLeftoversAsync("ReflogOnlyCommit");

    [Fact]
    public Task EmptyLeftoverFolder() =>
        AssertLeftoversAsync("EmptyLeftoverFolder", (LeftoverFolder, "repo-product-plan-205c69", SafeToRemove, null, Claude));

    [Fact]
    public Task ForgottenWorktreeFolder() =>
        AssertLeftoversAsync("ForgottenWorktreeFolder", (LeftoverFolder, "repo-claude", NeedsReview, null, null));

    [Fact]
    public Task DroppedCheckpointCommits() =>
        AssertLeftoversAsync("DroppedCheckpointCommits",
            // The "dirty-archive" commit names its own id, which reads as a session of its own.
            (LeftoverKind.OrphanedCommit, "1 checkpoint commit of session 9e1b7c3d", SafeToRemove, 1, Copilot),
            (LeftoverKind.OrphanedCommit, "3 checkpoint commits of session 2d5ea27e", SafeToRemove, 3, Copilot));

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

    public static TheoryData<string> AllScenarios => [.. FixtureRepoBuilder.ScenarioNames];

    /// <summary>A row Johan has to look at says what to look at, so it is something he can act on.</summary>
    [Theory]
    [MemberData(nameof(AllScenarios))]
    public async Task Every_row_that_needs_review_says_what_to_check(string scenario)
    {
        var rows = await ClassifyAsync(scenario);

        Assert.All(rows.Where(l => l.Verdict == NeedsReview), l => Assert.False(string.IsNullOrWhiteSpace(l.Check), $"{l.Kind} {l.Name} says nothing to check"));
    }

    [Theory]
    [MemberData(nameof(AllScenarios))]
    public async Task Every_row_but_a_folder_has_a_commit_to_show_in_History(string scenario)
    {
        var rows = await ClassifyAsync(scenario);

        Assert.All(rows.Where(l => l.Kind != LeftoverFolder), l => Assert.Matches("^[0-9a-f]{40}$", l.Commit ?? ""));
        Assert.All(rows.Where(l => l.Kind == LeftoverFolder), l => Assert.Null(l.Commit));
    }

    [Fact]
    public async Task The_check_says_in_plain_words_what_to_look_at()
    {
        Assert.Equal(
            "Look at the 3 commits only on this branch in History. Deleting it keeps them under a backup tag.",
            Assert.Single(await ClassifyAsync("AgentBranchUnmerged")).Check);
        Assert.Equal(
            "Its upstream was deleted, most likely after a squash merge. Check that the work of its 2 commits is in main.",
            Assert.Single(await ClassifyAsync("SquashMergedBranch")).Check);
        Assert.Null(Assert.Single(await ClassifyAsync("AgentBranchMerged")).Check);
    }

    [Fact]
    public void Every_scenario_has_a_test()
    {
        var tested = typeof(ScenarioVerdictTests).GetMethods().Select(m => m.Name).ToHashSet();

        Assert.All(FixtureRepoBuilder.ScenarioNames, name => Assert.Contains(name, tested));
    }
}
