using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers.Cleanup;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Leftovers.Tests;

/// <summary>The plan for every kind of leftover, from the fixture scenarios of milestone 1, as the table in milestone 5 has it.</summary>
public class CleanupPlannerTests(ScenarioSnapshots snapshots)
{
    private const string Date = "2026-03-01";
    private const string WhileGitKeepsThem = "Undo works while git still has the commits.";
    private static readonly FakeTimeProvider Time = new(FixtureRepoBuilder.DefaultNow);

    /// <summary>A step as text, so a whole plan compares in one go.</summary>
    private sealed record Step(string Kind, string Command, string Undo, string Expects = "", string? UndoNote = null, bool Optional = false);

    private static Step Describe(CleanupStep s) => new(
        s.Kind.ToString(),
        Text(s.Command) ?? $"recycle {s.Folder}",
        Text(s.Undo) ?? "",
        string.Join("; ", s.Expects.Select(ExpectText)),
        s.UndoNote,
        s.Optional);

    private static string? Text(GitCommandLine? command) =>
        command is null ? null : string.Join(' ', command.Arguments) + (command.Input.Count > 0 ? $" < {string.Join(" / ", command.Input)}" : "");

    private static string ExpectText(Expectation expectation) => expectation switch
    {
        Expectation.RefAt e => $"{e.FullName} at {e.ObjectId}",
        Expectation.WorktreeClean e => $"clean {e.Path}",
        Expectation.NoFilesInUse e => $"free {e.Path}",
        Expectation.StashPresent e => $"stash {e.CommitId}",
        Expectation.RefAbsent e => $"no {e.FullName}",
        Expectation.TagOn e => $"{e.FullName} on {e.CommitId}",
        Expectation.ObjectExists e => $"has {e.ObjectId}",
        Expectation.PathAbsent e => $"nothing at {e.Path}",
        Expectation.StashAbsent e => $"no stash {e.CommitId}",
        _ => expectation.ToString(),
    };

    /// <summary>What each step's undo expects, as text, in step order.</summary>
    private static async Task<List<string>> UndoExpectsAsync(Task<(RepoSnapshot Repo, CleanupPlan Plan)> planning)
    {
        var (_, plan) = await planning;
        return [.. Assert.Single(plan.Leftovers).Steps.Select(s => string.Join("; ", s.UndoExpects.Select(ExpectText)))];
    }

    /// <summary>Classifies the scenario, selects every leftover, and plans them all.</summary>
    private async Task<(RepoSnapshot Repo, CleanupPlan Plan)> PlanAsync(string scenario, Func<RepoSnapshot, RepoSnapshot>? change = null)
    {
        var repo = await snapshots.GetAsync(scenario);
        var leftovers = new LeftoverClassifier().Classify(repo, DiffractConfig.Default, Time);
        var planned = change?.Invoke(repo) ?? repo;
        return (repo, CleanupPlanner.Plan(planned, leftovers, DiffractConfig.Default, Time));
    }

    /// <summary>The one leftover's steps, as text.</summary>
    private async Task<(RepoSnapshot Repo, List<Step> Steps)> StepsAsync(string scenario)
    {
        var (repo, plan) = await PlanAsync(scenario);
        return (repo, [.. Assert.Single(plan.Leftovers).Steps.Select(Describe)]);
    }

    private static string BackupMessage(string what, int commits) =>
        $"Backup made by Diffract before removing {what}, with {(commits == 1 ? "1 commit" : $"{commits} commits")} that exist nowhere else.";

    [Fact]
    public async Task A_clean_repo_plans_nothing()
    {
        var (_, plan) = await PlanAsync("Clean");

        Assert.Empty(plan.Leftovers);
    }

    [Theory]
    [InlineData("ActiveAgentWorktree")]
    [InlineData("LockedWorktree")]
    [InlineData("UserDirtyWorktree")]
    public async Task Nothing_in_use_is_ever_planned(string scenario)
    {
        var (_, plan) = await PlanAsync(scenario);

        var refused = Assert.Single(plan.Leftovers);
        Assert.Equal((0, "In use, so it is never cleaned up."), (refused.Steps.Count, refused.Refused));
    }

    [Fact]
    public async Task A_merged_worktree_is_removed_when_no_file_is_in_use_then_its_branch_as_a_step_of_its_own()
    {
        var (repo, steps) = await StepsAsync("MergedWorktree");
        var w = repo.Worktrees.Single(w => w.Branch == "feature/done");

        Assert.Equal(
        [
            new("RemoveWorktree", $"worktree remove {w.Path}", $"worktree add {w.Path} feature/done", $"free {w.Path}; clean {w.Path}; refs/heads/feature/done at {w.Head}"),
            new("DeleteBranch", "branch -D feature/done", $"branch feature/done {w.Head}", $"refs/heads/feature/done at {w.Head}", Optional: true),
        ], steps);
    }

    [Fact]
    public async Task A_worktree_with_work_found_nowhere_else_gets_a_backup_tag_first()
    {
        var (repo, steps) = await StepsAsync("StaleAgentWorktree");
        var w = repo.Worktrees.Single(w => w.Branch == "claude/refactor-cart");
        var tag = $"archive/worktree/claude/refactor-cart-{Date}";

        Assert.Equal(new Step("Backup", $"tag -a {tag} {w.Head} -F - < {BackupMessage($"worktree {w.Path}", 2)}", $"tag -d {tag}", steps[1].Expects), steps[0]);
        Assert.Equal(["Backup", "RemoveWorktree", "DeleteBranch"], steps.Select(s => s.Kind));
    }

    [Fact]
    public async Task A_worktree_whose_folder_is_gone_is_forgotten_by_its_path_only_never_pruned()
    {
        var (repo, steps) = await StepsAsync("PrunableWorktree");
        var w = repo.Worktrees.Single(w => w.Branch == "feature/deleted");

        Assert.Equal(
            new Step("RemoveWorktree", $"worktree remove {w.Path}", "", $"refs/heads/feature/deleted at {w.Head}", "The folder was already gone, so undo brings back the branch, not the folder."),
            steps[0]);
        Assert.Equal(["RemoveWorktree", "DeleteBranch"], steps.Select(s => s.Kind));
    }

    [Fact]
    public async Task An_unmerged_branch_is_backed_up_then_deleted()
    {
        var (repo, steps) = await StepsAsync("AgentBranchUnmerged");
        var tip = repo.Branches.Single(b => b.Name == "copilot/add-logging").TargetId;
        var tag = $"archive/branch/copilot/add-logging-{Date}";

        Assert.Equal(
        [
            new("Backup", $"tag -a {tag} {tip} -F - < {BackupMessage("branch copilot/add-logging", 3)}", $"tag -d {tag}", $"refs/heads/copilot/add-logging at {tip}"),
            new("DeleteBranch", "branch -D copilot/add-logging", $"branch copilot/add-logging {tip}", $"refs/heads/copilot/add-logging at {tip}"),
        ], steps);
    }

    [Fact]
    public async Task A_branch_whose_upstream_is_gone_gets_no_backup_and_says_how_long_undo_works()
    {
        var (repo, steps) = await StepsAsync("SquashMergedBranch");
        var tip = repo.Branches.Single(b => b.Name == "feature/search").TargetId;

        Assert.Equal([new Step("DeleteBranch", "branch -D feature/search", $"branch feature/search {tip}", $"refs/heads/feature/search at {tip}", WhileGitKeepsThem)], steps);
    }

    [Fact]
    public async Task A_remote_tracking_branch_is_deleted_here_only_and_undo_creates_it_again()
    {
        var (repo, steps) = await StepsAsync("RemoteAgentBranch");
        var tip = repo.Branches.Single(b => b.Name == "origin/copilot/fix-readme").TargetId;
        const string Ref = "refs/remotes/origin/copilot/fix-readme";

        Assert.Equal(
            [new Step("DeleteRemoteBranch", "branch -d -r origin/copilot/fix-readme", $"update-ref --stdin < start / create {Ref} {tip} / prepare / commit", $"{Ref} at {tip}")],
            steps);
    }

    [Fact]
    public async Task A_stash_is_always_backed_up_then_dropped_and_undo_stores_it_on_top_again()
    {
        var (repo, steps) = await StepsAsync("ToolStash");
        var stash = Assert.Single(repo.Stashes);
        var tag = $"archive/stash/{stash.CommitId[..12]}-{Date}";

        Assert.Equal(
        [
            new("Backup", $"tag -a {tag} {stash.CommitId} -F - < {BackupMessage($"stash \"{stash.Message}\"", 1)}", $"tag -d {tag}", $"stash {stash.CommitId}"),
            new("DropStash", "stash drop stash@{0}", $"stash store -m {stash.Message} {stash.CommitId}", $"stash {stash.CommitId}", "Undo puts it back on top of the list."),
        ], steps);
    }

    [Fact]
    public async Task A_checkpoint_session_is_deleted_in_one_transaction_from_the_values_its_refs_had()
    {
        var (repo, steps) = await StepsAsync("StaleCheckpointSession");
        var refs = repo.OtherRefs.Where(r => r.FullName.Contains("3f2b8c1e", StringComparison.Ordinal)).ToList();
        string Lines(string verb) => string.Join(" / ", ["start", .. refs.Select(r => $"{verb} {r.FullName} {r.TargetId}"), "prepare", "commit"]);

        Assert.Equal(
            [new Step("DeleteRefs", $"update-ref --stdin < {Lines("delete")}", $"update-ref --stdin < {Lines("create")}", string.Join("; ", refs.Select(r => $"{r.FullName} at {r.TargetId}")), WhileGitKeepsThem)],
            steps);
        Assert.NotEmpty(refs);
    }

    [Fact]
    public async Task An_old_backup_tag_is_deleted_and_undo_puts_back_the_tag_object_itself()
    {
        var (repo, steps) = await StepsAsync("OldArchiveTag");
        var tag = repo.Tags.Single(t => t.Name == "archive/old-idea");

        Assert.Equal(
            [new Step("DeleteTag", "tag -d archive/old-idea", $"update-ref --stdin < start / create refs/tags/archive/old-idea {tag.ObjectId} / prepare / commit", $"refs/tags/archive/old-idea at {tag.ObjectId}", WhileGitKeepsThem)],
            steps);
        Assert.NotEqual(tag.TargetId, tag.ObjectId);
    }

    [Fact]
    public async Task A_leftover_folder_goes_to_the_Recycle_Bin_which_is_its_undo()
    {
        var (repo, steps) = await StepsAsync("LeftoverWorktreeFolder");
        var folder = Assert.Single(repo.LeftoverFolders).Path;

        Assert.Equal([new Step("RecycleFolder", $"recycle {folder}", "", $"free {folder}", "Restore it from the Recycle Bin.")], steps);
    }

    [Fact]
    public async Task One_orphaned_commit_is_kept_under_a_tag_of_its_own_and_nothing_is_removed()
    {
        var (repo, plan) = await PlanAsync("OrphanedCommit");
        var commit = repo.UnreachableCommits.Single(c => c.Subject == "Experiment").Id;
        var tag = $"archive/orphaned/{commit[..12]}-{Date}";

        var step = Assert.Single(plan.Leftovers.Single(l => l.Leftover.Name == "49919a09 Experiment").Steps);

        Assert.Equal(
            new Step("Backup", $"tag -a {tag} {commit} -F - < Kept by Diffract: 1 commit that was on no branch, tag, stash or reflog.", $"tag -d {tag}",
                $"has {commit}", "Undo takes the tag away, and git may then delete the commit again."),
            Describe(step));
        Assert.Equal([$"refs/tags/{tag} on {commit}"], step.UndoExpects.Select(ExpectText));
    }

    [Fact]
    public async Task Several_orphaned_commits_get_a_commit_made_to_hold_them_all_under_one_tag()
    {
        var (repo, plan) = await PlanAsync("OrphanedCommit");
        var commits = repo.UnreachableCommits.Where(c => c.Subject.StartsWith("Agent host session", StringComparison.Ordinal)).ToList();
        var newest = commits.MaxBy(c => c.CommittedAt)!.Id;

        var step = Assert.Single(plan.Leftovers.Single(l => l.Leftover.Name == "2 commits of session d2061d20").Steps);

        Assert.Equal(StepKind.KeepCommits, step.Kind);
        Assert.Equal(["commit-tree", "4b825dc642cb6eb9a060e54bf8d69288fbee4904", .. commits.SelectMany(c => new[] { "-p", c.Id }), "-F", "-"], step.Command!.Arguments);
        Assert.Equal(["Diffract kept these commits"], step.Command.Input);
        Assert.Equal(new KeepTag($"archive/orphaned/{newest[..12]}-{Date}", "Kept by Diffract: 2 commits that were on no branch, tag, stash or reflog."), step.Keep);
        Assert.Equal([$"refs/tags/archive/orphaned/{newest[..12]}-{Date}"], step.UndoExpects.Select(e => ((Expectation.RefPresent)e).FullName));
    }

    public static TheoryData<string> AllScenarios => [.. FixtureRepoBuilder.ScenarioNames];

    [Theory]
    [MemberData(nameof(AllScenarios))]
    public async Task Every_command_and_undo_in_every_plan_is_on_the_cleanup_allowlist(string scenario)
    {
        var (_, plan) = await PlanAsync(scenario);

        foreach (var command in plan.Leftovers.SelectMany(l => l.Steps).SelectMany(s => new[] { s.Command, s.Undo }).OfType<GitCommandLine>())
            Assert.True(CleanupPolicy.IsAllowed(command, out var reason), $"{Text(command)}: {reason}");
    }

    [Fact]
    public async Task Undoing_a_deleted_branch_expects_its_name_free_and_its_commit_kept_and_the_backup_goes_only_while_it_is_on_that_commit()
    {
        var repo = await snapshots.GetAsync("AgentBranchUnmerged");
        var tip = repo.Branches.Single(b => b.Name == "copilot/add-logging").TargetId;

        var undo = await UndoExpectsAsync(PlanAsync("AgentBranchUnmerged"));

        Assert.Equal([$"refs/tags/archive/branch/copilot/add-logging-{Date} on {tip}", $"no refs/heads/copilot/add-logging; has {tip}"], undo);
    }

    [Fact]
    public async Task Undoing_a_removed_worktree_expects_nothing_at_its_path_and_its_branch_back_where_it_was()
    {
        var repo = await snapshots.GetAsync("MergedWorktree");
        var w = repo.Worktrees.Single(w => w.Branch == "feature/done");

        var undo = await UndoExpectsAsync(PlanAsync("MergedWorktree"));

        Assert.Equal([$"nothing at {w.Path}; refs/heads/feature/done at {w.Head}", $"no refs/heads/feature/done; has {w.Head}"], undo);
    }

    [Fact]
    public async Task Undoing_a_dropped_stash_expects_it_not_in_the_list_and_its_commit_kept()
    {
        var stash = Assert.Single((await snapshots.GetAsync("ToolStash")).Stashes);

        var undo = await UndoExpectsAsync(PlanAsync("ToolStash"));

        Assert.Equal($"no stash {stash.CommitId}; has {stash.CommitId}", undo[1]);
    }

    [Fact]
    public async Task Undoing_a_checkpoint_session_expects_every_name_free_and_every_commit_kept()
    {
        var refs = (await snapshots.GetAsync("StaleCheckpointSession")).OtherRefs.Where(r => r.FullName.Contains("3f2b8c1e", StringComparison.Ordinal)).ToList();

        var undo = await UndoExpectsAsync(PlanAsync("StaleCheckpointSession"));

        Assert.Equal(string.Join("; ", [.. refs.Select(r => $"no {r.FullName}"), .. refs.Select(r => $"has {r.TargetId}")]), Assert.Single(undo));
    }

    [Fact]
    public async Task A_backup_tag_name_already_taken_gets_a_number()
    {
        var taken = new TagInfo($"archive/branch/copilot/add-logging-{Date}", "", "", null, false, null);

        var (_, plan) = await PlanAsync("AgentBranchUnmerged", repo => repo with { Tags = [.. repo.Tags, taken] });

        Assert.StartsWith($"tag -a archive/branch/copilot/add-logging-{Date}-2 ", Text(plan.Leftovers[0].Steps[0].Command));
    }

    [Fact]
    public async Task A_leftover_the_snapshot_no_longer_has_is_refused()
    {
        var (_, plan) = await PlanAsync("AgentBranchUnmerged", repo => repo with { Branches = [.. repo.Branches.Where(b => b.Name != "copilot/add-logging")] });

        Assert.Equal("It is no longer there. Refresh to see the repo as it is now.", Assert.Single(plan.Leftovers).Refused);
    }
}
