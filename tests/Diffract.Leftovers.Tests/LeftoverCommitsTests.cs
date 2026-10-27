using Diffract.Git;

namespace Diffract.Leftovers.Tests;

/// <summary>"Show what is in it": the commits only a leftover holds, read from fixture repos.</summary>
public class LeftoverCommitsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The subjects of what the scenario's leftover with this name holds, newest first.</summary>
    private static async Task<(List<string> Subjects, bool More)> ContentAsync(CleanupFixture fixture, Func<Leftover, bool> which)
    {
        var repo = await fixture.SnapshotAsync();
        var leftover = (await fixture.LeftoversAsync()).Single(which);
        var range = LeftoverCommits.RangeOf(repo, leftover, DiffractConfig.Default) ?? throw new InvalidOperationException($"No range for {leftover.Name}");
        var content = await LeftoverCommits.ReadAsync(new GitRunner(fixture.RepoPath, fixture.Git), leftover.Kind, range, None);
        return ([.. content.Commits.Select(c => c.Subject)], content.More);
    }

    private static async Task<List<string>> SubjectsAsync(string scenario, Func<Leftover, bool>? which = null)
    {
        using var fixture = await CleanupFixture.BuildAsync(scenario);
        return (await ContentAsync(fixture, which ?? (_ => true))).Subjects;
    }

    [Fact]
    public async Task An_unmerged_branch_shows_the_commits_only_it_has()
    {
        Assert.Equal(["Add logging, step 3", "Add logging, step 2", "Add logging, step 1"], await SubjectsAsync("AgentBranchUnmerged"));
    }

    [Fact]
    public async Task A_merged_branch_holds_nothing_of_its_own()
    {
        Assert.Empty(await SubjectsAsync("AgentBranchMerged"));
    }

    [Fact]
    public async Task A_stale_worktree_shows_the_commits_only_its_branch_has()
    {
        Assert.Equal(["Refactor cart totals", "Refactor cart"], await SubjectsAsync("StaleAgentWorktree"));
    }

    [Fact]
    public async Task A_stash_shows_itself_without_gits_own_bookkeeping_commits()
    {
        Assert.Equal(["On main: autostash"], await SubjectsAsync("ToolStash"));
    }

    [Fact]
    public async Task A_checkpoint_session_shows_its_checkpoints()
    {
        var subjects = await SubjectsAsync("StaleCheckpointSession");

        Assert.Equal(3, subjects.Count);
        Assert.All(subjects, s => Assert.StartsWith("copilot checkpoint: 3f2b8c1e", s));
    }

    [Fact]
    public async Task Each_orphaned_commit_row_shows_its_own_commits()
    {
        using var fixture = await CleanupFixture.BuildAsync("OrphanedCommit");

        var single = await ContentAsync(fixture, l => l.Name.EndsWith(" Experiment", StringComparison.Ordinal));
        var session = await ContentAsync(fixture, l => l.Name.StartsWith("2 commits of session", StringComparison.Ordinal));

        Assert.Equal(["Experiment"], single.Subjects);
        Assert.Equal(2, session.Subjects.Count);
        Assert.All(session.Subjects, s => Assert.StartsWith("Agent host session d2061d20", s));
    }

    [Fact]
    public async Task A_folder_holds_no_commits()
    {
        using var fixture = await CleanupFixture.BuildAsync("LeftoverWorktreeFolder");
        var leftover = Assert.Single(await fixture.LeftoversAsync());

        Assert.Null(LeftoverCommits.RangeOf(await fixture.SnapshotAsync(), leftover, DiffractConfig.Default));
    }

    [Fact]
    public async Task More_than_fifty_shows_the_newest_fifty_and_says_there_are_more()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchUnmerged");
        // Old enough that the branch is still stale, so still a leftover.
        fixture.Repo.Clock.Now = Diffract.Fixtures.FixtureRepoBuilder.DefaultNow.AddDays(-20);
        await fixture.GitAsync("switch", "-q", "copilot/add-logging");
        for (var i = 0; i < LeftoverCommits.Shown; i++)
            await fixture.GitAsync("commit", "-q", "--allow-empty", "-m", $"Step {i}");
        await fixture.GitAsync("switch", "-q", "main");

        var (subjects, more) = await ContentAsync(fixture, _ => true);

        Assert.Equal((LeftoverCommits.Shown, true), (subjects.Count, more));
        Assert.Equal($"Step {LeftoverCommits.Shown - 1}", subjects[0]);
    }
}
