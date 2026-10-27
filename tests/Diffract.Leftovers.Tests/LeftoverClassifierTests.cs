using Diffract.Leftovers.Rules;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Leftovers.Tests;

public class LeftoverClassifierTests
{
    private static readonly FakeTimeProvider Time = new(Snap.Now);
    private static readonly DiffractConfig Config = DiffractConfig.Default;

    private static IReadOnlyList<Leftover> Classify(Git.Snapshot.RepoSnapshot repo, string? runningFrom = null) =>
        new LeftoverClassifier().Classify(repo, Config, Time, runningFrom);

    private const string Wt = @"C:\src\repo\.claude\worktrees\agent";

    // --- overlaps ---------------------------------------------------------------

    [Fact]
    public void Item_reported_by_several_rules_appears_once_with_the_most_cautious_verdict()
    {
        // Stale and unmerged (needs review), but locked (in use).
        var repo = Snap.Repo(
            [Snap.Worktree(Wt, "claude/agent") with { IsLocked = true, LastActivityAt = Snap.Now.AddDays(-20) }],
            [Snap.Branch("claude/agent", risk: 2)]);

        var leftover = Assert.Single(Classify(repo));

        Assert.Equal((Verdict.InUse, "Locked in git", 2), (leftover.Verdict, leftover.Reason, leftover.CommitsAtRisk));
    }

    [Fact]
    public void On_a_tie_the_earlier_rule_gives_the_reason()
    {
        var repo = Snap.Repo([Snap.Worktree(Wt, "claude/agent") with { DirtyFileCount = 2, LastActivityAt = Snap.Now.AddMinutes(-3) }],
            [Snap.Branch("claude/agent")]);

        Assert.Equal("Files changed 3 minutes ago", Assert.Single(Classify(repo)).Reason);
    }

    [Fact]
    public void Activity_under_a_minute_ago_reads_naturally()
    {
        var repo = Snap.Repo([Snap.Worktree(Wt, "claude/agent") with { LastActivityAt = Snap.Now.AddSeconds(-20) }], [Snap.Branch("claude/agent")]);

        Assert.Equal("Files changed less than a minute ago", Assert.Single(Classify(repo)).Reason);
    }

    [Fact]
    public void Sorted_by_verdict_then_kind_then_name()
    {
        var repo = Snap.Repo(
            [Snap.Worktree(@"C:\wt\b", "b") with { DirtyFileCount = 1 }, Snap.Worktree(@"C:\wt\a", "a")],
            [Snap.Branch("b"), Snap.Branch("a", merged: true), Snap.Branch("z", merged: true), Snap.Branch("y", merged: true)]);

        var order = Classify(repo).Select(l => (l.Verdict, l.Kind, l.Name));

        Assert.Equal(
            [
                (Verdict.SafeToRemove, LeftoverKind.Worktree, "a"),
                (Verdict.SafeToRemove, LeftoverKind.LocalBranch, "y"),
                (Verdict.SafeToRemove, LeftoverKind.LocalBranch, "z"),
                (Verdict.InUse, LeftoverKind.Worktree, "b"),
            ],
            order);
    }

    // --- never reported ---------------------------------------------------------

    [Fact]
    public void Main_worktree_is_never_a_leftover()
    {
        var repo = Snap.Repo();
        repo = repo with { Worktrees = [repo.Worktrees[0] with { DirtyFileCount = 5, IsLocked = true, LastActivityAt = Snap.Now }] };

        Assert.Empty(Classify(repo));
    }

    [Fact]
    public void Worktree_diffract_runs_from_is_never_a_leftover()
    {
        var repo = Snap.Repo([Snap.Worktree(Wt, "claude/agent")], [Snap.Branch("claude/agent", merged: true)]);

        Assert.Single(Classify(repo));
        Assert.Empty(Classify(repo, runningFrom: Wt));
        Assert.Empty(Classify(repo, runningFrom: Wt.ToUpperInvariant() + @"\src\"));
        Assert.Single(Classify(repo, runningFrom: Wt + "-other"));
    }

    [Fact]
    public void Hidden_worktree_is_not_reported_while_clean()
    {
        const string prewarm = @"C:\repos\copilot-worktrees\repo\copilot-prewarm-curly-waddle";
        var clean = Snap.Repo([Snap.Worktree(prewarm, null) with { LastActivityAt = Snap.Now.AddDays(-30) }]);
        var dirty = clean with { Worktrees = [clean.Worktrees[0], clean.Worktrees[1] with { DirtyFileCount = 1 }] };

        Assert.Empty(Classify(clean));
        var leftover = Assert.Single(Classify(dirty));
        Assert.Equal((Verdict.InUse, "GitHub Copilot"), (leftover.Verdict, leftover.Agent));
    }

    [Fact]
    public void Linked_worktree_on_main_is_not_safe_to_remove()
    {
        Assert.Empty(Classify(Snap.Repo([Snap.Worktree(@"C:\wt\main-copy", "main")])));
    }

    // --- boundaries -------------------------------------------------------------

    [Theory]
    [InlineData(10, Verdict.InUse)]
    [InlineData(11, null)]
    public void Active_within_minutes_is_inclusive(int minutesAgo, Verdict? expected)
    {
        var repo = Snap.Repo([Snap.Worktree(Wt, "claude/agent") with { LastActivityAt = Snap.Now.AddMinutes(-minutesAgo) }], [Snap.Branch("claude/agent")]);

        Assert.Equal(expected, Classify(repo).SingleOrDefault()?.Verdict);
    }

    [Theory]
    [InlineData(7, Verdict.NeedsReview)]
    [InlineData(6, null)]
    public void Stale_after_days_is_inclusive(int daysAgo, Verdict? expected)
    {
        var worktree = Snap.Repo([Snap.Worktree(Wt, "claude/agent") with { LastActivityAt = Snap.Now.AddDays(-daysAgo) }], [Snap.Branch("claude/agent", risk: 1)]);
        var branch = Snap.Repo(branches: [Snap.Branch("copilot/x", risk: 1, lastCommit: Snap.Now.AddDays(-daysAgo))]);

        Assert.Equal(expected, Classify(worktree).SingleOrDefault()?.Verdict);
        Assert.Equal(expected, Classify(branch).SingleOrDefault()?.Verdict);
    }

    [Fact]
    public void Gone_upstream_is_safe_and_keeps_its_commits_at_risk()
    {
        var gone = Snap.Branch("feature/x", risk: 2) with { Upstream = "origin/feature/x", IsUpstreamGone = true, LastCommitAt = Snap.Now.AddDays(-30) };

        var leftover = Assert.Single(Classify(Snap.Repo(branches: [gone])));

        Assert.Equal((Verdict.SafeToRemove, 2), (leftover.Verdict, leftover.CommitsAtRisk));
        Assert.Equal("Upstream branch was deleted, probably merged; 2 commits exist nowhere else", leftover.Reason);
    }

    [Fact]
    public void Old_backup_tag_is_reviewed_after_the_configured_days()
    {
        var tag = new Git.Snapshot.TagInfo("archive/x", "refs/tags/archive/x", "abc", Snap.Now.AddDays(-30), false, 1);
        var notArchive = tag with { Name = "v1.0", FullName = "refs/tags/v1.0" };

        Assert.Single(Classify(Snap.Repo() with { Tags = [tag, notArchive] }));
        Assert.Empty(Classify(Snap.Repo() with { Tags = [tag with { CreatedAt = Snap.Now.AddDays(-29) }] }));
    }

    // --- checkpoint sessions ----------------------------------------------------

    [Fact]
    public void Checkpoints_are_one_row_per_session_aged_by_the_newest()
    {
        var refs = new[]
        {
            Snap.Checkpoint("old-session", 1, Snap.Now.AddDays(-12)),
            Snap.Checkpoint("old-session", 2, Snap.Now.AddDays(-8)),
            Snap.Checkpoint("mixed-session", 1, Snap.Now.AddDays(-20)),
            Snap.Checkpoint("mixed-session", 2, Snap.Now.AddDays(-1)),
            new Git.Snapshot.RefInfo("refs/notes/commits", "n1", Snap.Now.AddDays(-100)),
        };

        var leftover = Assert.Single(Classify(Snap.Repo() with { OtherRefs = refs }));

        Assert.Equal((LeftoverKind.CheckpointSession, "old-session", "2 checkpoints, newest is 8 days old"), (leftover.Kind, leftover.Name, leftover.Reason));
    }

    [Fact]
    public void Checkpoint_refs_under_the_spec_layout_are_grouped_too()
    {
        var refs = new[] { new Git.Snapshot.RefInfo("refs/checkpoints/s1/1/a", "c1", Snap.Now.AddDays(-9)) };

        var leftover = Assert.Single(Classify(Snap.Repo() with { OtherRefs = refs }));

        Assert.Equal(("s1", null), (leftover.Name, leftover.Agent));
    }

    // --- stashes and remote branches --------------------------------------------

    [Fact]
    public void Only_stashes_from_tools_are_reported()
    {
        var stashes = new[]
        {
            new Git.Snapshot.StashInfo(0, "stash@{0}", "s0", Snap.Now.AddDays(-1), "WIP on main: 1234 my own work"),
            new Git.Snapshot.StashInfo(1, "stash@{1}", "s1", Snap.Now.AddDays(-2), "On main: AutoStash before rebase"),
        };

        var leftover = Assert.Single(Classify(Snap.Repo() with { Stashes = stashes }));

        Assert.Equal("stash@{1}", leftover.Name);
    }

    [Fact]
    public void Merged_remote_branches_are_reported_only_when_they_match_remote_branch_patterns()
    {
        static Git.Snapshot.BranchInfo Remote(string name, bool merged) =>
            Snap.Branch(name, merged) with { FullName = $"refs/remotes/{name}", IsRemote = true };

        var repo = Snap.Repo(branches: [Remote("origin/main", true), Remote("origin/release/1.0", true), Remote("origin/claude/x", true), Remote("origin/copilot/y", false)]);

        var leftover = Assert.Single(Classify(repo));

        Assert.Equal(("origin/claude/x", "Claude Code", "Merged into main, still on origin"), (leftover.Name, leftover.Agent, leftover.Reason));
    }

    // --- attribution ------------------------------------------------------------

    [Fact]
    public void Agent_is_a_hint_that_never_changes_the_verdict()
    {
        var noAgents = Config with { Agents = [] };
        var repo = Snap.Repo([Snap.Worktree(@"C:\repos\copilot-worktrees\repo\me-curly-waddle", "renamed-branch")], [Snap.Branch("renamed-branch", merged: true)]);

        var withAgents = Assert.Single(Classify(repo));
        var without = Assert.Single(new LeftoverClassifier().Classify(repo, noAgents, Time));

        Assert.Equal("GitHub Copilot", withAgents.Agent);
        Assert.Null(without.Agent);
        Assert.Equal(withAgents with { Agent = null }, without);
    }

    [Fact]
    public void Orphaned_commits_get_a_row_each_unless_an_agents_session_groups_them()
    {
        const string Session = "d2061d20-a6c1-46ea-9cc3-fcf61843f825";
        var repo = Snap.Repo() with
        {
            UnreachableCommits =
            [
                new("c1", Snap.Now.AddMinutes(-2), $"Agent host session {Session} - baseline checkpoint"),
                new("c2", Snap.Now.AddMinutes(-1), $"Agent host session {Session} - checkpoint 1"),
                new("c3", Snap.Now, "Experiment"),
                new("c4", Snap.Now, "Another experiment"),
            ],
        };

        var rows = Classify(repo);

        Assert.Equal(
            [("2 commits of session d2061d20", "GitHub Copilot", 2, "c2"), ("c3 Experiment", (string?)null, 1, "c3"), ("c4 Another experiment", null, 1, "c4")],
            rows.Select(l => (l.Name, l.Agent, l.CommitsAtRisk ?? 0, l.Commit)).OrderBy(r => r.Item1, StringComparer.Ordinal));
        Assert.All(rows, l => Assert.Equal((LeftoverKind.OrphanedCommit, Verdict.NeedsReview), (l.Kind, l.Verdict)));
    }

    [Fact]
    public void An_agents_commit_with_no_session_id_in_its_subject_is_a_row_of_its_own()
    {
        var repo = Snap.Repo() with { UnreachableCommits = [new("c1", Snap.Now, "Agent host session s - baseline checkpoint")] };

        var row = Assert.Single(Classify(repo));

        Assert.Equal(("c1 Agent host session s - baseline checkpoint", "GitHub Copilot"), (row.Name, row.Agent));
    }

    [Fact]
    public void Dropped_checkpoint_commits_are_safe_only_through_their_own_setting()
    {
        var repo = Snap.Repo() with { UnreachableCommits = [new("c1", Snap.Now, "copilot checkpoint: s @ 1")] };
        var hintOnly = Config with { CheckpointCommitPrefixes = [] };

        var withSetting = Assert.Single(Classify(repo));
        var without = Assert.Single(new LeftoverClassifier().Classify(repo, hintOnly, Time));

        Assert.Equal(("c1 copilot checkpoint: s @ 1", Verdict.SafeToRemove, "GitHub Copilot"), (withSetting.Name, withSetting.Verdict, withSetting.Agent));
        Assert.Equal(("c1 copilot checkpoint: s @ 1", Verdict.NeedsReview, "GitHub Copilot"), (without.Name, without.Verdict, without.Agent));
    }

    [Fact]
    public void Rules_can_be_chosen()
    {
        var repo = Snap.Repo(branches: [Snap.Branch("a", merged: true)]);

        Assert.Empty(new LeftoverClassifier([new OrphanedCommitRule()]).Classify(repo, Config, Time));
    }
}
