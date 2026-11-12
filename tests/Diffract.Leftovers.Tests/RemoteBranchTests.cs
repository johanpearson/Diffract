using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers.Cleanup;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Leftovers.Tests;

/// <summary>
/// Deleting a branch on a remote, and undoing it, against a bare repo in a temp folder that stands in for the remote.
/// Nothing here ever touches a real remote.
/// </summary>
public sealed class RemoteBranchTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly FakeTimeProvider Time = new(FixtureRepoBuilder.DefaultNow);

    private readonly TempDirectory _temp = new();
    private readonly OperationLog _log;
    private FixtureRepo _mine = null!;
    private FixtureRepo _remote = null!;
    private string _feature = "";

    public RemoteBranchTests() => _log = new OperationLog(_temp.Combine("oplog"));

    public void Dispose() => _temp.Dispose();

    private GitRunnerOptions Git => new() { Environment = IsolatedGit.Environment(_temp), TimeProvider = Time };

    private CleanupExecutor Executor() => new(_mine.Path, new CleanupOptions { Git = Git, Log = _log });

    /// <summary>
    /// A repo with a bare remote that has main (its default) and "feature/räksmörgås", which holds a commit that exists
    /// nowhere else once the local branch is gone. Diffract's copies of both are up to date.
    /// </summary>
    private async Task StartAsync()
    {
        _remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        _mine = await FixtureRepo.InitAsync(_temp, "mine");
        await _mine.CommitAsync("a.txt", "a\n", "First");
        await _mine.GitAsync("remote", "add", "origin", _remote.Path);
        await _mine.GitAsync("switch", "-q", "-c", "feature/räksmörgås");
        _feature = await _mine.CommitAsync("b.txt", "b\n", "Only on the remote");
        await _mine.GitAsync("push", "-q", "origin", "main", "feature/räksmörgås");
        await _mine.GitAsync("switch", "-q", "main");
        await _mine.GitAsync("branch", "-q", "-D", "feature/räksmörgås");
    }

    private async Task<RepoSnapshot> SnapshotAsync() =>
        (await new RepoSnapshotBuilder(Git, DiffractConfig.Default.ToSnapshotOptions()).BuildAsync(_mine.Path, Ct)).Snapshot;

    private async Task<CleanupPlan> PlanAsync(string branch = "feature/räksmörgås")
    {
        var snapshot = await SnapshotAsync();
        return RemoteBranchPlanner.Plan(snapshot, await RemoteBranchPlanner.ReadAsync(snapshot, Git, new RemoteTarget("origin", branch), Ct), branch, Time);
    }

    private async Task<string> OnRemoteAsync() => (await _remote.GitAsync("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads")).StandardOutput;

    private async Task<string> TrackingAsync() => (await _mine.GitAsync("for-each-ref", "--format=%(refname) %(objectname)", "refs/remotes")).StandardOutput;

    private static List<string> Outcomes(CleanupResult result) => [.. result.Steps.Select(s => $"{s.Kind} {s.Outcome} {s.Reason}".Trim())];

    private async Task<CleanupResult> ApplyAsync() => await Executor().ApplyAsync(await PlanAsync(), Ct);

    private async Task<string> TagsAsync() => (await _mine.GitAsync("tag")).StandardOutput;

    [Fact]
    public async Task Plans_a_backup_tag_and_then_the_delete_on_the_remote()
    {
        await StartAsync();

        var steps = Assert.Single((await PlanAsync()).Leftovers).Steps;

        Assert.Equal([StepKind.Backup, StepKind.DeleteOnRemote], steps.Select(s => s.Kind));
        Assert.Equal(["push", "origin", "--delete", "feature/räksmörgås"], steps[1].Command!.Arguments);
        Assert.Equal(["push", "origin", $"{_feature}:refs/heads/feature/räksmörgås"], steps[1].Undo!.Arguments);
    }

    [Fact]
    public async Task The_delete_says_which_remote_it_changes_and_that_it_is_for_everyone()
    {
        await StartAsync();

        var delete = Assert.Single((await PlanAsync()).Leftovers).Steps[1];

        Assert.Equal("origin", delete.Remote);
        Assert.Equal("Delete branch feature/räksmörgås on origin, for everyone", delete.Description);
    }

    [Fact]
    public async Task Planning_changes_nothing_here_or_there()
    {
        await StartAsync();
        var (remote, tracking) = (await OnRemoteAsync(), await TrackingAsync());

        await PlanAsync();

        Assert.Equal(remote, await OnRemoteAsync());
        Assert.Equal(tracking, await TrackingAsync());
    }

    [Fact]
    public async Task Deletes_the_branch_on_the_remote_and_only_that_one()
    {
        await StartAsync();

        var result = await ApplyAsync();

        Assert.Equal(["Backup Done", "DeleteOnRemote Done"], Outcomes(result));
        Assert.DoesNotContain("feature/räksmörgås", await OnRemoteAsync());
        Assert.Contains("refs/heads/main", await OnRemoteAsync());
    }

    [Fact]
    public async Task Git_removes_the_copy_here_when_the_push_succeeded_and_the_backup_tag_stays()
    {
        await StartAsync();

        await ApplyAsync();

        Assert.DoesNotContain("feature/räksmörgås", await TrackingAsync());
        Assert.Contains("archive/remote/origin/feature/räksmörgås", await TagsAsync());
    }

    [Fact]
    public async Task Undo_pushes_the_branch_back_to_the_commit_it_had()
    {
        await StartAsync();
        var applied = await Executor().ApplyAsync(await PlanAsync(), Ct);

        var undone = await Executor().UndoAsync(applied.CleanupId, Ct);

        Assert.All(Outcomes(undone), outcome => Assert.EndsWith("Done", outcome));
        Assert.Contains($"refs/heads/feature/räksmörgås {_feature}", await OnRemoteAsync());
        Assert.Contains($"refs/remotes/origin/feature/räksmörgås {_feature}", await TrackingAsync());
    }

    [Fact]
    public async Task Undo_leaves_a_branch_that_was_created_again_on_the_remote_alone()
    {
        await StartAsync();
        var applied = await Executor().ApplyAsync(await PlanAsync(), Ct);
        var other = await _mine.CommitAsync("c.txt", "c\n", "Someone else's");
        await _mine.GitAsync("push", "-q", "origin", $"{other}:refs/heads/feature/räksmörgås");

        var undone = await Executor().UndoAsync(applied.CleanupId, Ct);

        Assert.Contains(undone.Steps, s => s.Outcome == StepOutcome.Skipped && s.Reason!.Contains("has a branch feature/räksmörgås again"));
        Assert.Contains($"refs/heads/feature/räksmörgås {other}", await OnRemoteAsync());
    }

    [Fact]
    public async Task A_branch_that_moved_on_the_remote_since_the_dialog_is_left_alone()
    {
        await StartAsync();
        var plan = await PlanAsync();
        await _mine.GitAsync("switch", "-q", "-c", "work", "origin/feature/räksmörgås");
        var moved = await _mine.CommitAsync("d.txt", "d\n", "Pushed meanwhile");
        await _mine.GitAsync("push", "-q", "origin", "work:feature/räksmörgås");
        await _mine.GitAsync("switch", "-q", "main");

        var result = await Executor().ApplyAsync(plan, Ct);

        Assert.Contains(result.Steps, s => s.Outcome == StepOutcome.Skipped && s.Reason!.Contains("moved"));
        Assert.DoesNotContain(result.Steps, s => s.Kind == StepKind.DeleteOnRemote && s.Outcome == StepOutcome.Done);
        Assert.Contains($"refs/heads/feature/räksmörgås {moved}", await OnRemoteAsync());
    }

    /// <summary>Planned while the remote is there, then applied after it has gone.</summary>
    private async Task<CleanupResult> ApplyToUnreachableRemoteAsync()
    {
        var plan = await PlanAsync();
        Directory.Move(_remote.Path, _temp.Combine("moved-away.git"));
        return await Executor().ApplyAsync(plan, Ct);
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_skips_the_step_with_git_words()
    {
        await StartAsync();

        var result = await ApplyToUnreachableRemoteAsync();

        Assert.Equal(StepOutcome.Skipped, result.Steps[0].Outcome);
        Assert.Contains("Git could not ask origin which branches it has", result.Steps[0].Reason);
        Assert.Contains("This is what git said", result.Steps[0].Reason);
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_changes_nothing_here()
    {
        await StartAsync();
        var tracking = await TrackingAsync();

        var result = await ApplyToUnreachableRemoteAsync();

        Assert.DoesNotContain(result.Steps, s => s.Outcome == StepOutcome.Done);
        Assert.Equal(tracking, await TrackingAsync());
        Assert.DoesNotContain("archive/", await TagsAsync());
    }

    /// <summary>Planned while deletes are allowed, then applied after the remote started to refuse them.</summary>
    private async Task<StepResult> ApplyToRefusingRemoteAsync()
    {
        var plan = await PlanAsync();
        await _remote.GitAsync("config", "receive.denyDeletes", "true");
        return (await Executor().ApplyAsync(plan, Ct)).Steps.Single(s => s.Kind == StepKind.DeleteOnRemote);
    }

    [Fact]
    public async Task A_remote_that_refuses_the_delete_ends_with_its_words()
    {
        await StartAsync();

        var delete = await ApplyToRefusingRemoteAsync();

        Assert.Equal(StepOutcome.Failed, delete.Outcome);
        Assert.StartsWith("Git refused:", delete.Reason);
        Assert.Contains("remote rejected", delete.Reason);
    }

    [Fact]
    public async Task A_remote_that_refuses_the_delete_keeps_the_branch_and_the_copy_here()
    {
        await StartAsync();
        var tracking = await TrackingAsync();

        await ApplyToRefusingRemoteAsync();

        Assert.Contains("refs/heads/feature/räksmörgås", await OnRemoteAsync());
        Assert.Equal(tracking, await TrackingAsync());
    }

    [Fact]
    public async Task The_default_branch_of_the_remote_is_never_planned()
    {
        await StartAsync();
        await _remote.GitAsync("symbolic-ref", "HEAD", "refs/heads/main");

        var plan = await PlanAsync("main");

        var refused = Assert.Single(plan.Leftovers);
        Assert.Empty(refused.Steps);
        Assert.Equal("main is the default branch of origin. Diffract does not delete it.", refused.Refused);
    }

    [Fact]
    public async Task Whichever_branch_the_remote_names_as_its_default_is_refused()
    {
        await StartAsync();
        await _remote.GitAsync("symbolic-ref", "HEAD", "refs/heads/feature/räksmörgås");

        var plan = await PlanAsync();

        Assert.Contains("is the default branch", Assert.Single(plan.Leftovers).Refused);
    }

    [Fact]
    public async Task Nothing_is_planned_when_the_remote_names_no_default_branch()
    {
        await StartAsync();
        await _remote.GitAsync("symbolic-ref", "HEAD", "refs/heads/not-there");

        var plan = await PlanAsync();

        Assert.Contains("could not tell which branch is the default", Assert.Single(plan.Leftovers).Refused);
    }

    [Fact]
    public async Task A_branch_the_remote_no_longer_has_is_refused_with_a_hint()
    {
        await StartAsync();
        await _remote.GitAsync("branch", "-q", "-D", "feature/räksmörgås");

        var plan = await PlanAsync();

        Assert.Contains("does not have feature/räksmörgås any more", Assert.Single(plan.Leftovers).Refused);
    }

    [Fact]
    public async Task A_copy_that_is_behind_the_remote_is_refused_until_it_is_fetched()
    {
        await StartAsync();
        var elsewhere = await FixtureRepo.InitAsync(_temp, "elsewhere");
        await elsewhere.GitAsync("remote", "add", "origin", _remote.Path);
        await elsewhere.GitAsync("fetch", "-q", "origin");
        await elsewhere.GitAsync("switch", "-q", "-c", "work", "origin/feature/räksmörgås");
        await elsewhere.CommitAsync("e.txt", "e\n", "Newer");
        await elsewhere.GitAsync("push", "-q", "origin", "work:feature/räksmörgås");

        var plan = await PlanAsync();

        Assert.Contains("Fetch first", Assert.Single(plan.Leftovers).Refused);
    }

    [Fact]
    public async Task A_branch_whose_work_is_kept_elsewhere_needs_no_backup_tag()
    {
        await StartAsync();
        await _mine.GitAsync("branch", "-q", "keeps-it", _feature);

        var plan = await PlanAsync();

        Assert.Equal([StepKind.DeleteOnRemote], Assert.Single(plan.Leftovers).Steps.Select(s => s.Kind));
    }
}
