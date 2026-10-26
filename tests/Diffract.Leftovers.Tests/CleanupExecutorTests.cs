using Diffract.Leftovers.Cleanup;

namespace Diffract.Leftovers.Tests;

/// <summary>Every kind of worktree is removed, then undone, and the repo ends where it started. A class of its own, so it runs next to the refs.</summary>
public class WorktreeRoundTripTests
{
    [Theory]
    [InlineData("MergedWorktree")]
    [InlineData("StaleAgentWorktree")]
    [InlineData("AwkwardPaths")]
    [InlineData("DetachedWorktree")]
    public Task Every_kind_of_worktree_is_removed_and_undo_puts_the_repo_back_where_it_started(string scenario) =>
        CleanupExecutorTests.RoundTripAsync(scenario);
}

/// <summary>Every kind of ref leftover is removed, then undone, and the repo ends where it started.</summary>
public class RefRoundTripTests
{
    [Theory]
    [InlineData("AgentBranchMerged")]
    [InlineData("AgentBranchUnmerged")]
    [InlineData("SquashMergedBranch")]
    [InlineData("RemoteAgentBranch")]
    [InlineData("ToolStash")]
    [InlineData("StaleCheckpointSession")]
    [InlineData("OldArchiveTag")]
    public Task Every_kind_of_ref_is_removed_and_undo_puts_the_repo_back_where_it_started(string scenario) =>
        CleanupExecutorTests.RoundTripAsync(scenario);
}

/// <summary>Applying and undoing in fixture repos: the order, the log, the backup tag's identity, and what cannot be undone.</summary>
public class CleanupExecutorTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The steps' outcomes, so a failing test says which step did what and why.</summary>
    private static List<string> Outcomes(CleanupResult result) => [.. result.Steps.Select(s => $"{s.Kind} {s.Outcome} {s.Reason}".Trim())];

    private static void AllDone(CleanupResult result) =>
        Assert.All(Outcomes(result), outcome => Assert.Matches(@"^\w+ Done$", outcome));

    /// <summary>
    /// Up to 4 git calls per step: a removed worktree's checks take 3 and the removal 1. A cleanup that makes a backup
    /// reads the name and email once, 2 more. An undo reads only the log to know what to do.
    /// </summary>
    private static void WithinBudget(int calls, CleanupResult result, bool backups) =>
        Assert.InRange(calls, 0, 4 * result.Steps.Count + (backups ? 2 : 0));

    /// <summary>Removes every leftover, checks they are gone, undoes it all, and checks the repo is back where it started.</summary>
    internal static async Task RoundTripAsync(string scenario)
    {
        using var fixture = await CleanupFixture.BuildAsync(scenario);
        var before = await fixture.StateAsync();
        var plan = await fixture.PlanAsync();
        var executor = fixture.Executor();

        var applied = await executor.ApplyAsync(plan, None);
        var applyCalls = fixture.Calls;

        AllDone(applied);
        WithinBudget(applyCalls, applied, backups: applied.Steps.Any(s => s.Kind == StepKind.Backup));
        var removed = plan.Leftovers.Where(l => l.Refused is null).Select(l => (l.Leftover.Kind, l.Leftover.Name)).ToList();
        Assert.NotEmpty(removed);
        Assert.DoesNotContain(await fixture.LeftoversAsync(), l => removed.Contains((l.Kind, l.Name)));

        var undone = await executor.UndoAsync(applied.CleanupId, None);

        AllDone(undone);
        WithinBudget(fixture.Calls - applyCalls, undone, backups: false);
        Assert.Equal(before, await fixture.StateAsync());
    }

    [Fact]
    public async Task Undo_runs_the_steps_in_reverse_order_so_the_branch_is_back_before_its_worktree()
    {
        using var fixture = await CleanupFixture.BuildAsync("StaleAgentWorktree");
        var executor = fixture.Executor();
        var applied = await executor.ApplyAsync(await fixture.PlanAsync(), None);

        var undone = await executor.UndoAsync(applied.CleanupId, None);

        Assert.Equal(["Backup", "RemoveWorktree", "DeleteBranch"], applied.Steps.Select(s => s.Kind.ToString()));
        Assert.Equal(["DeleteBranch", "RemoveWorktree", "Backup"], undone.Steps.Select(s => s.Kind.ToString()));
        Assert.Equal("Undo: Delete branch claude/refactor-cart", undone.Steps[0].Description);
    }

    [Fact]
    public async Task A_worktree_whose_folder_is_gone_gets_its_branch_back_and_the_result_says_the_folder_cannot()
    {
        using var fixture = await CleanupFixture.BuildAsync("PrunableWorktree");
        var branch = await fixture.GitAsync("rev-parse", "refs/heads/feature/deleted");
        var executor = fixture.Executor();
        var applied = await executor.ApplyAsync(await fixture.PlanAsync(), None);
        AllDone(applied);
        WithinBudget(fixture.Calls, applied, backups: false);
        Assert.Empty(await fixture.LeftoversAsync());

        var undone = await executor.UndoAsync(applied.CleanupId, None);

        Assert.Equal(
            ["DeleteBranch Done", "RemoveWorktree Skipped The folder was already gone, so undo brings back the branch, not the folder."],
            Outcomes(undone));
        Assert.Equal(branch, await fixture.GitAsync("rev-parse", "refs/heads/feature/deleted"));
    }

    [Fact]
    public async Task A_leftover_folder_goes_to_the_Recycle_Bin_and_undo_says_to_restore_it_from_there()
    {
        using var fixture = await CleanupFixture.BuildAsync("EmptyLeftoverFolder");
        var plan = await fixture.PlanAsync();
        var folder = Assert.Single(Assert.Single(plan.Leftovers).Steps).Folder!;
        var executor = fixture.Executor();

        var applied = await executor.ApplyAsync(plan, None);

        AllDone(applied);
        Assert.Equal(0, fixture.Calls);
        Assert.False(Directory.Exists(folder));
        var refused = await Assert.ThrowsAsync<CleanupLogException>(() => executor.UndoAsync(applied.CleanupId, None));
        Assert.Equal("Nothing this cleanup did can be undone by git. Restore folders from the Recycle Bin.", refused.Message);
    }

    [Fact]
    public async Task A_cleanup_undone_once_cannot_be_undone_again_and_an_undo_cannot_be_undone()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchMerged");
        var executor = fixture.Executor();
        var applied = await executor.ApplyAsync(await fixture.PlanAsync(), None);
        var undone = await executor.UndoAsync(applied.CleanupId, None);

        var again = await Assert.ThrowsAsync<CleanupLogException>(() => executor.UndoAsync(applied.CleanupId, None));
        var ofUndo = await Assert.ThrowsAsync<CleanupLogException>(() => executor.UndoAsync(undone.CleanupId, None));
        var unknown = await Assert.ThrowsAsync<CleanupLogException>(() => executor.UndoAsync("nope", None));

        Assert.Equal("Everything in this cleanup that can be undone is undone already.", again.Message);
        Assert.Equal("An undo cannot be undone. Clean up again instead.", ofUndo.Message);
        Assert.Equal("The operation log of this repo has no cleanup nope.", unknown.Message);
    }

    [Fact]
    public async Task A_backup_tag_is_made_by_Diffract_when_the_repo_has_no_name_and_email()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchUnmerged");

        await fixture.Executor().ApplyAsync(await fixture.PlanAsync(), None);

        Assert.Equal("Diffract <diffract@localhost>", await fixture.GitAsync("for-each-ref", "--format=%(taggername) %(taggeremail)", "refs/tags/archive/"));
    }

    [Fact]
    public async Task A_backup_tag_is_made_by_whoever_the_repo_says_when_it_has_a_name_and_email()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchUnmerged");
        await fixture.GitAsync("config", "user.name", "Someone");
        await fixture.GitAsync("config", "user.email", "someone@example.com");

        await fixture.Executor().ApplyAsync(await fixture.PlanAsync(), None);

        // The fixture's environment sets the committer, and git lets that win over the repo's config.
        Assert.Equal("Fixture Author <fixture@example.com>", await fixture.GitAsync("for-each-ref", "--format=%(taggername) %(taggeremail)", "refs/tags/archive/"));
    }

    [Fact]
    public async Task Every_step_is_in_the_operation_log_with_what_takes_it_back()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchUnmerged");
        var applied = await fixture.Executor().ApplyAsync(await fixture.PlanAsync(), None);

        var logged = Assert.Single(fixture.Log.History(fixture.RepoPath));

        Assert.Equal((applied.CleanupId, null), (logged.Started.CleanupId, logged.Started.Undoes));
        Assert.Equal(["LocalBranch copilot/add-logging"], logged.Started.Leftovers);
        Assert.Equal(applied.Steps, logged.Steps.Select(s => s.Result));
        Assert.Equal(["tag -d", "branch copilot/add-logging"], logged.Steps.Select(s => string.Join(' ', s.Undo!.Arguments.Take(2))));
    }

    /// <summary>A refused leftover is not in the result: the dialog already said why.</summary>
    [Fact]
    public async Task Nothing_in_use_is_run()
    {
        using var fixture = await CleanupFixture.BuildAsync("DetachedWorktree");
        var before = await fixture.StateAsync();
        var plan = await fixture.PlanAsync(l => l.Verdict == Verdict.InUse);

        var applied = await fixture.Executor().ApplyAsync(plan, None);

        Assert.Empty(applied.Steps);
        Assert.Equal(before, await fixture.StateAsync());
    }

}
