using Diffract.Fixtures;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Leftovers.Tests;

/// <summary>Every rule fires on its own fixture scenario and stays silent on a clean repo.</summary>
public class RuleTests(ScenarioSnapshots snapshots)
{
    private static readonly FakeTimeProvider Time = new(FixtureRepoBuilder.DefaultNow);

    private static readonly (string Rule, string Scenario, LeftoverKind Kind, Verdict Verdict)[] FiringCases =
    [
        ("RecentActivityRule", "ActiveAgentWorktree", LeftoverKind.Worktree, Verdict.InUse),
        ("LockedWorktreeRule", "LockedWorktree", LeftoverKind.Worktree, Verdict.InUse),
        ("DirtyWorktreeRule", "UserDirtyWorktree", LeftoverKind.Worktree, Verdict.InUse),
        ("PrunableWorktreeRule", "PrunableWorktree", LeftoverKind.PrunableWorktree, Verdict.SafeToRemove),
        ("MergedWorktreeRule", "MergedWorktree", LeftoverKind.Worktree, Verdict.SafeToRemove),
        ("StaleWorktreeRule", "StaleAgentWorktree", LeftoverKind.Worktree, Verdict.NeedsReview),
        ("DetachedWorktreeRule", "DetachedWorktree", LeftoverKind.Worktree, Verdict.NeedsReview),
        ("MergedBranchRule", "AgentBranchMerged", LeftoverKind.LocalBranch, Verdict.SafeToRemove),
        ("StaleBranchRule", "AgentBranchUnmerged", LeftoverKind.LocalBranch, Verdict.NeedsReview),
        ("MergedRemoteBranchRule", "RemoteAgentBranch", LeftoverKind.RemoteBranch, Verdict.SafeToRemove),
        ("ToolStashRule", "ToolStash", LeftoverKind.Stash, Verdict.NeedsReview),
        ("CheckpointSessionRule", "StaleCheckpointSession", LeftoverKind.CheckpointSession, Verdict.NeedsReview),
        ("OrphanedCommitRule", "OrphanedCommit", LeftoverKind.OrphanedCommit, Verdict.NeedsReview),
        ("OrphanedCommitRule", "DroppedCheckpointCommits", LeftoverKind.OrphanedCommit, Verdict.SafeToRemove),
        ("OldBackupTagRule", "OldArchiveTag", LeftoverKind.BackupTag, Verdict.NeedsReview),
        ("LeftoverFolderRule", "LeftoverWorktreeFolder", LeftoverKind.LeftoverFolder, Verdict.NeedsReview),
        ("LeftoverFolderRule", "EmptyLeftoverFolder", LeftoverKind.LeftoverFolder, Verdict.SafeToRemove),
        ("LeftoverFolderRule", "ForgottenWorktreeFolder", LeftoverKind.LeftoverFolder, Verdict.NeedsReview),
    ];

    private static readonly string[] RuleNames = [.. LeftoverClassifier.DefaultRules.Select(r => r.GetType().Name)];

    public static TheoryData<string, string, LeftoverKind, Verdict> Fires
    {
        get
        {
            var data = new TheoryData<string, string, LeftoverKind, Verdict>();
            foreach (var (rule, scenario, kind, verdict) in FiringCases)
                data.Add(rule, scenario, kind, verdict);
            return data;
        }
    }

    public static TheoryData<string> AllRules => [.. RuleNames];

    private static ILeftoverRule Rule(string name) => LeftoverClassifier.DefaultRules.Single(r => r.GetType().Name == name);

    [Theory]
    [MemberData(nameof(Fires))]
    public async Task Fires_on_its_scenario(string rule, string scenario, LeftoverKind kind, Verdict verdict)
    {
        var leftovers = Rule(rule).Evaluate(await snapshots.GetAsync(scenario), DiffractConfig.Default, Time).ToList();

        Assert.NotEmpty(leftovers);
        Assert.All(leftovers, l => Assert.Equal((kind, verdict), (l.Kind, l.Verdict)));
    }

    [Theory]
    [MemberData(nameof(AllRules))]
    public async Task Silent_on_a_clean_repo(string rule)
    {
        Assert.Empty(Rule(rule).Evaluate(await snapshots.GetAsync("Clean"), DiffractConfig.Default, Time));
    }

    [Fact]
    public void Every_rule_has_a_firing_test()
    {
        var covered = FiringCases.Select(c => c.Rule).ToHashSet();

        Assert.All(RuleNames, name => Assert.Contains(name, covered));
    }
}
