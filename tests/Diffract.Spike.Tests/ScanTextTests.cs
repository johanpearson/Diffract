using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;
using static Diffract.Leftovers.LeftoverKind;
using static Diffract.Leftovers.Verdict;

namespace Diffract.Spike.Tests;

public class ScanTextTests
{
    private static readonly RepoSnapshot Snapshot = new(
        @"C:\src\checkout", "main", new GitVersion(2, 47, 1), [], [], [], [], [], [], [],
        new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A leftover with no agent, nothing at risk and the reason "Reason". Tests set what they look at with <c>with</c>.</summary>
    private static Leftover Item(LeftoverKind kind, string name, Verdict verdict) =>
        new(kind, name, null, null, verdict, 0, "Reason");

    private static string[] Write(IReadOnlyList<Leftover> leftovers, IReadOnlyList<GitCallTiming>? timings = null)
    {
        var output = new StringWriter();
        ScanText.Write(output, new ScanReport(Snapshot, leftovers, TimeSpan.FromMilliseconds(412.4), timings));
        return output.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
    }

    [Fact]
    public void Matches_the_layout_in_the_milestone_spec()
    {
        var lines = Write(
        [
            Item(Worktree, "copilot/update-deps", SafeToRemove) with { Agent = "GitHub Copilot", Reason = "Branch merged into main, worktree is clean" },
            Item(PrunableWorktree, "claude/spike-cache", SafeToRemove) with { Agent = "Claude Code", Reason = "Folder was deleted outside git" },
            Item(Worktree, "claude/refactor-cart", NeedsReview) with
            {
                Agent = "Claude Code", CommitsAtRisk = 2, Reason = "No activity for 9 days, 2 commits exist nowhere else",
            },
            Item(Worktree, "claude/fix-flaky-login", InUse) with { Agent = "Claude Code", Reason = "Files changed 2 minutes ago" },
        ]);

        Assert.Equal(
            [
                @"Diffract spike  C:\src\checkout",
                "main: main   git: 2.47.1   snapshot: 412 ms",
                "",
                "Verdict       Kind      Name                    Agent           At risk  Reason",
                "Safe          Worktree  copilot/update-deps     GitHub Copilot  0        Branch merged into main, worktree is clean",
                "Safe          Prunable  claude/spike-cache      Claude Code     0        Folder was deleted outside git",
                "Needs review  Worktree  claude/refactor-cart    Claude Code     2        No activity for 9 days, 2 commits exist nowhere else",
                "In use        Worktree  claude/fix-flaky-login  Claude Code     0        Files changed 2 minutes ago",
                "",
                "4 leftovers: 2 safe, 1 needs review, 1 in use",
            ],
            lines);
    }

    [Fact]
    public void Risk_that_was_not_counted_shows_a_dash()
    {
        var lines = Write([Item(LocalBranch, "feature/x", NeedsReview), Item(LeftoverFolder, "repo-claude", NeedsReview) with { CommitsAtRisk = null }]);

        Assert.Equal("Needs review  Branch  feature/x    -      0        Reason", lines[4]);
        Assert.Equal("Needs review  Folder  repo-claude  -      -        Reason", lines[5]);
    }

    [Fact]
    public void Missing_agent_shows_a_dash()
    {
        var lines = Write([Item(LocalBranch, "feature/x", SafeToRemove)]);

        Assert.Equal("Safe     Branch  feature/x  -      0        Reason", lines[4]);
    }

    [Fact]
    public void No_leftovers_says_so()
    {
        Assert.Equal("No leftovers.", Write([])[^1]);
    }

    [Theory]
    [InlineData(1, 0, 0, "1 leftover: 1 safe")]
    [InlineData(0, 2, 0, "2 leftovers: 2 needs review")]
    [InlineData(0, 0, 3, "3 leftovers: 3 in use")]
    [InlineData(1, 1, 1, "3 leftovers: 1 safe, 1 needs review, 1 in use")]
    public void Summary_leaves_out_empty_verdicts(int safe, int review, int inUse, string expected)
    {
        var leftovers = Enumerable.Repeat(SafeToRemove, safe)
            .Concat(Enumerable.Repeat(NeedsReview, review))
            .Concat(Enumerable.Repeat(InUse, inUse))
            .Select((v, i) => Item(Stash, $"stash@{{{i}}}", v))
            .ToList();

        Assert.Equal(expected, ScanText.Summary(leftovers));
    }

    [Fact]
    public void Every_kind_and_verdict_has_a_label()
    {
        Assert.All(Enum.GetValues<LeftoverKind>(), k => Assert.False(string.IsNullOrWhiteSpace(ScanText.KindLabel(k))));
        Assert.Equal(Enum.GetValues<LeftoverKind>().Length, Enum.GetValues<LeftoverKind>().Select(ScanText.KindLabel).Distinct().Count());
        Assert.Equal(["Safe", "Needs review", "In use"], Enum.GetValues<Verdict>().Select(ScanText.VerdictLabel));
    }

    [Theory]
    [InlineData("git version", "git version")]
    [InlineData("git rev-list --left-right --count refs/heads/main...918dc612ca211d831b114bfc76dab25678b68e51",
        "git rev-list --left-right --count refs/heads/main...918dc612ca211d831b114bfc76dab25678b68e51")]
    [InlineData("git log --no-walk=unsorted --format=%H %ct 6ae5471f2b6dbcd31159fca4a519ba8b4b302bf3",
        "git log --no-walk=unsorted --format=%H %ct 6ae5471f2b6dbcd31159fca4a519ba8b4b302bf3")]
    [InlineData("git log --no-walk=unsorted --format=%H 6ae5471f2b6dbcd31159fca4a519ba8b4b302bf3 121b471d56e4bbf60714f3f4fd2e359b6e6d340e 918dc612ca211d831b114bfc76dab25678b68e51",
        "git log --no-walk=unsorted --format=%H 6ae5471f2b6dbcd31159fca4a519ba8b4b302bf3 (+2 more ids)")]
    public void Long_lists_of_commit_ids_are_shortened(string command, string expected)
    {
        Assert.Equal(expected, ScanText.Shorten(command));
    }

    [Fact]
    public void Timings_are_sorted_slowest_first_with_fsck_on_its_own_line()
    {
        var lines = Write([],
        [
            new("git version", TimeSpan.FromMilliseconds(20)),
            new("git fsck --unreachable --no-reflogs --no-progress", TimeSpan.FromMilliseconds(800)),
            new("git status --porcelain=v2 --branch -z", TimeSpan.FromMilliseconds(45)),
        ]);

        Assert.Equal(
            [
                "Git calls, slowest first:",
                "   800 ms  git fsck --unreachable --no-reflogs --no-progress",
                "    45 ms  git status --porcelain=v2 --branch -z",
                "    20 ms  git version",
                "",
                "fsck: 800 ms",
                "Other calls: 2, slowest 45 ms",
            ],
            lines[^7..]);
    }
}
