using Diffract.Fixtures;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Snapshot;

/// <summary>
/// Starting a git process is the main cost of a snapshot: 170 to 360 ms each on a laptop with antivirus.
/// So the number of calls is capped instead of the wall-clock time.
/// </summary>
public class GitCallBudgetTests
{
    /// <summary>
    /// 8 repo-wide calls, up to 2 per worktree (status and ahead/behind), 1 per unmerged local branch or tag
    /// (commits at risk), and 1 per batch of 200 unreachable commits.
    /// </summary>
    public static int Budget(RepoSnapshot snapshot) =>
        8
        + 2 * snapshot.Worktrees.Count
        + snapshot.Branches.Count(b => !b.IsRemote && !b.IsMergedIntoMain)
        + snapshot.Tags.Count(t => !t.IsMergedIntoMain)
        + (snapshot.UnreachableCommits.Count + 199) / 200;

    public static TheoryData<string> Scenarios => [.. FixtureRepoBuilder.ScenarioNames];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Snapshot_stays_within_the_git_call_budget(string scenario)
    {
        using var temp = new TempDirectory();
        var built = await new FixtureRepoBuilder(temp).BuildAsync(scenario);
        var options = new SnapshotOptions { WorktreeContainerPatterns = ["**/copilot-worktrees/*", "**/.claude/worktrees"] };

        var result = await new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(temp) }, options)
            .BuildAsync(built.RepoPath, TestContext.Current.CancellationToken);

        Assert.InRange(result.Timings.Count, 1, Budget(result.Snapshot));
    }
}
