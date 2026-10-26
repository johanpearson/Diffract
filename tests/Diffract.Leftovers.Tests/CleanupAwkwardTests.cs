using Diffract.Leftovers.Cleanup;

namespace Diffract.Leftovers.Tests;

/// <summary>What happens when the repo changes between planning and applying, as it does while agents work.</summary>
public class CleanupAwkwardTests
{
    private const string EarlierStep = "Skipped, as an earlier step for the same leftover did not run.";
    private static readonly CancellationToken None = CancellationToken.None;

    private static List<string> Outcomes(CleanupResult result) => [.. result.Steps.Select(s => $"{s.Kind} {s.Outcome} {s.Reason}".Trim())];

    /// <summary>Holds a file open, as an editor or a running build does.</summary>
    private static FileStream Hold(string folder, string file) =>
        new(Path.Combine(folder, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    [Fact]
    public async Task A_branch_that_moved_after_planning_is_left_and_gets_no_backup_tag()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchUnmerged");
        var plan = await fixture.PlanAsync();
        var main = await fixture.GitAsync("rev-parse", "main");
        await fixture.GitAsync("update-ref", "refs/heads/copilot/add-logging", main);
        var before = await fixture.StateAsync();

        var applied = await fixture.Executor().ApplyAsync(plan, None);

        Assert.Matches(@"^Backup Skipped Branch copilot/add-logging moved: it is at \w{8} now, not \w{8}\.$", Outcomes(applied)[0]);
        Assert.Equal($"DeleteBranch Skipped {EarlierStep}", Outcomes(applied)[1]);
        Assert.Equal(before, await fixture.StateAsync());
    }

    [Fact]
    public async Task A_worktree_that_turned_dirty_after_planning_is_left_with_its_branch()
    {
        using var fixture = await CleanupFixture.BuildAsync("StaleAgentWorktree");
        var plan = await fixture.PlanAsync();
        File.WriteAllText(Path.Combine(Assert.Single(plan.Leftovers).Leftover.Path!, "new.txt"), "agent at work");
        var before = await fixture.StateAsync();

        var applied = await fixture.Executor().ApplyAsync(plan, None);

        Assert.Equal(
            ["Backup Skipped The worktree has changes now, which git does not remove.", $"RemoveWorktree Skipped {EarlierStep}", $"DeleteBranch Skipped {EarlierStep}"],
            Outcomes(applied));
        Assert.Equal(before, await fixture.StateAsync());
    }

    [Fact]
    public async Task A_worktree_with_a_file_held_open_is_left_and_the_other_worktrees_are_removed()
    {
        using var fixture = await CleanupFixture.BuildAsync("AwkwardPaths");
        var plan = await fixture.PlanAsync();
        var held = plan.Leftovers.Single(l => l.Leftover.Name == "feature/smörgås").Leftover.Path!;
        using var file = Hold(held, "readme.md");

        var applied = await fixture.Executor().ApplyAsync(plan, None);

        Assert.Contains($"RemoveWorktree Skipped A program is using files in {held}.", Outcomes(applied));
        Assert.Equal(["feature/smörgås"], (await fixture.LeftoversAsync()).Select(l => l.Name));
        Assert.Equal(4, applied.Steps.Count(s => s.Outcome == StepOutcome.Done));
    }

    [Fact]
    public async Task A_file_opened_right_after_the_check_leaves_the_folder_and_keeps_the_branch()
    {
        using var fixture = await CleanupFixture.BuildAsync("MergedWorktree");
        var plan = await fixture.PlanAsync();
        var path = Assert.Single(plan.Leftovers).Leftover.Path!;
        var opensAfterCheck = new OpensAfterCheck();

        var applied = await fixture.Executor(opensAfterCheck).ApplyAsync(plan, None);
        opensAfterCheck.Dispose();

        Assert.Equal(
        [
            $"RemoveWorktree Failed Git stopped part way, as a program is using files in {path}. The folder was left with those files, and shows up as a leftover folder on the next scan. The branch is kept.",
            $"DeleteBranch Skipped {EarlierStep}",
        ], Outcomes(applied));
        Assert.True(Directory.Exists(path));
        Assert.NotEmpty(await fixture.GitAsync("rev-parse", "refs/heads/feature/done"));
    }

    /// <summary>The check passes, then a program opens a file, as an agent's build might.</summary>
    private sealed class OpensAfterCheck : IFolderOperations, IDisposable
    {
        private FileStream? _held;

        public string? InUse(string path)
        {
            var why = WindowsFolders.Instance.InUse(path);
            _held = Hold(path, "feature.txt");
            return why;
        }

        public string? Recycle(string path) => WindowsFolders.Instance.Recycle(path);

        public void Dispose() => _held?.Dispose();
    }

    [Fact]
    public async Task A_leftover_folder_with_a_file_held_open_stays_where_it_is()
    {
        using var fixture = await CleanupFixture.BuildAsync("LeftoverWorktreeFolder");
        var plan = await fixture.PlanAsync();
        var folder = Assert.Single(plan.Leftovers).Leftover.Path!;
        using var file = Hold(folder, Path.Combine("src", "App.cs"));

        var applied = await fixture.Executor().ApplyAsync(plan, None);

        Assert.Equal([$"RecycleFolder Skipped A program is using files in {folder}."], Outcomes(applied));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_stash_that_moved_down_the_list_is_dropped_by_its_commit_not_its_old_place()
    {
        using var fixture = await CleanupFixture.BuildAsync("ToolStash");
        var plan = await fixture.PlanAsync();
        fixture.Repo.WriteFile("readme.md", "# Fixture\nAnother agent\n");
        await fixture.GitAsync("stash", "push", "-q", "-m", "newer");

        var applied = await fixture.Executor().ApplyAsync(plan, None);

        Assert.Equal(["Backup Done", "DropStash Done"], Outcomes(applied));
        Assert.EndsWith(" On main: newer", (await fixture.StateAsync()).Stashes);
    }

    [Fact]
    public async Task A_stash_already_gone_is_skipped_and_gets_no_backup_tag()
    {
        using var fixture = await CleanupFixture.BuildAsync("ToolStash");
        var plan = await fixture.PlanAsync();
        await fixture.GitAsync("stash", "drop", "-q");

        var applied = await fixture.Executor().ApplyAsync(plan, None);

        Assert.Equal(["Backup Skipped The stash is no longer in the list.", $"DropStash Skipped {EarlierStep}"], Outcomes(applied));
        Assert.Empty(await fixture.GitAsync("tag", "--list"));
    }

    [Fact]
    public async Task Undo_after_git_removed_the_commits_says_they_cannot_come_back()
    {
        using var fixture = await CleanupFixture.BuildAsync("SquashMergedBranch");
        var executor = fixture.Executor();
        var applied = await executor.ApplyAsync(await fixture.PlanAsync(), None);
        await fixture.GitAsync("reflog", "expire", "--expire=now", "--all");
        await fixture.GitAsync("gc", "-q", "--prune=now");

        var undone = await executor.UndoAsync(applied.CleanupId, None);

        Assert.Matches(@"^DeleteBranch Skipped Git no longer has \w{8}, so it cannot be put back\.$", Assert.Single(Outcomes(undone)));
    }

    [Fact]
    public async Task Undo_leaves_a_branch_made_again_meanwhile_and_keeps_the_backup_tag()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchUnmerged");
        var executor = fixture.Executor();
        var applied = await executor.ApplyAsync(await fixture.PlanAsync(), None);
        await fixture.GitAsync("branch", "copilot/add-logging", "main");

        var undone = await executor.UndoAsync(applied.CleanupId, None);

        Assert.Equal(["DeleteBranch Skipped Branch copilot/add-logging is there again, so it was left as it is.", $"Backup Skipped {EarlierStep}"], Outcomes(undone));
        Assert.NotEmpty(await fixture.GitAsync("tag", "--list", "archive/*"));
    }

    [Fact]
    public async Task An_undo_that_stopped_part_way_can_be_tried_again_and_does_only_what_is_left()
    {
        using var fixture = await CleanupFixture.BuildAsync("AgentBranchUnmerged");
        var before = await fixture.StateAsync();
        var executor = fixture.Executor();
        var applied = await executor.ApplyAsync(await fixture.PlanAsync(), None);
        await fixture.GitAsync("branch", "copilot/add-logging", "main");
        await executor.UndoAsync(applied.CleanupId, None);
        await fixture.GitAsync("branch", "-D", "copilot/add-logging");

        var again = await executor.UndoAsync(applied.CleanupId, None);

        Assert.Equal(["DeleteBranch Done", "Backup Done"], Outcomes(again));
        Assert.Equal(before, await fixture.StateAsync());
    }
}
