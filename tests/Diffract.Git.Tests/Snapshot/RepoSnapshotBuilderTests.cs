using Diffract.Fixtures;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Snapshot;

public class RepoSnapshotBuilderTests
{
    private static readonly DateTimeOffset Jan15 = IsolatedGit.DefaultDate;

    private static async Task<SnapshotResult> BuildAsync(TempDirectory temp, string path, SnapshotOptions? options = null) =>
        await new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(temp) }, options ?? new SnapshotOptions())
            .BuildAsync(path, TestContext.Current.CancellationToken);

    private static async Task<RepoSnapshot> SnapshotAsync(TempDirectory temp, string path, SnapshotOptions? options = null) =>
        (await BuildAsync(temp, path, options)).Snapshot;

    /// <summary>A commit that no ref or reflog points to, the way agent tools make checkpoints.</summary>
    private static async Task<string> CommitTreeAsync(FixtureRepo repo, string message = "Checkpoint", string? parent = null) =>
        (await repo.GitAsync(["commit-tree", "HEAD^{tree}", "-m", message, .. parent is null ? Array.Empty<string>() : ["-p", parent]])).StandardOutput.Trim();

    private static async Task<FixtureRepo> RepoWithCommitAsync(TempDirectory temp, string name = "repo", string mainBranch = "main")
    {
        var repo = await FixtureRepo.InitAsync(temp, name, mainBranch);
        await repo.CommitAsync("readme.md", "hello", "Initial commit");
        return repo;
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // --- basics -----------------------------------------------------------------

    [Fact]
    public async Task Clean_repo()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Equal(Full(repo.Path), snapshot.RepoRoot);
        Assert.Equal("main", snapshot.MainBranch);
        Assert.True(snapshot.GitVersion.IsSupported);
        var main = Assert.Single(snapshot.Worktrees);
        Assert.True(main.IsMain);
        Assert.Equal(("main", 0, 0, 0), (main.Branch, main.DirtyFileCount, main.AheadOfMain, main.BehindMain));
        Assert.Equal(Jan15, main.LastCommitAt);
        var branch = Assert.Single(snapshot.Branches);
        Assert.Equal(("main", true, 0), (branch.Name, branch.IsMergedIntoMain, branch.CommitsAtRisk));
        Assert.Empty(snapshot.Tags);
        Assert.Empty(snapshot.Stashes);
        Assert.Empty(snapshot.OtherRefs);
        Assert.Empty(snapshot.UnreachableCommits);
        Assert.Empty(snapshot.LeftoverFolders);
    }

    [Fact]
    public async Task Taken_at_comes_from_the_time_provider()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        var builder = new RepoSnapshotBuilder(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(temp), TimeProvider = time, Timeout = Timeout.InfiniteTimeSpan },
            new SnapshotOptions());

        var result = await builder.BuildAsync(repo.Path, TestContext.Current.CancellationToken);

        Assert.Equal(time.GetUtcNow(), result.Snapshot.TakenAt);
    }

    [Fact]
    public async Task Works_from_a_linked_worktree_and_reports_the_main_worktree_as_root()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature", temp.Combine("wt"));

        var snapshot = await SnapshotAsync(temp, temp.Combine("wt"));

        Assert.Equal(Full(repo.Path), snapshot.RepoRoot);
        Assert.Equal(2, snapshot.Worktrees.Count);
    }

    [Fact]
    public async Task Folder_that_is_not_a_repo()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("plain"));

        await Assert.ThrowsAsync<NotAGitRepositoryException>(() => SnapshotAsync(temp, temp.Combine("plain")));
        await Assert.ThrowsAsync<NotAGitRepositoryException>(() => SnapshotAsync(temp, temp.Combine("missing")));
    }

    [Fact]
    public void Refuses_a_writable_runner()
    {
        Assert.Throws<ArgumentException>(() => new RepoSnapshotBuilder(new GitRunnerOptions { ReadOnly = false }, new SnapshotOptions()));
    }

    [Fact]
    public async Task Records_a_timing_for_every_git_call()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);

        var timings = (await BuildAsync(temp, repo.Path)).Timings;

        Assert.Contains(timings, t => t.Command.StartsWith("git fsck", StringComparison.Ordinal));
        Assert.Contains(timings, t => t.Command.StartsWith("git worktree list", StringComparison.Ordinal));
        Assert.All(timings, t => Assert.True(t.Duration > TimeSpan.Zero));
    }

    // --- main branch ------------------------------------------------------------

    [Fact]
    public async Task Main_is_origin_head_when_set()
    {
        using var temp = new TempDirectory();
        var origin = await FixtureRepo.InitAsync(temp, "origin.git", bare: true);
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("branch", "develop");
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "origin", "main", "develop");
        await repo.GitAsync("remote", "set-head", "origin", "develop");

        Assert.Equal("develop", (await SnapshotAsync(temp, repo.Path)).MainBranch);
    }

    [Fact]
    public async Task Main_is_init_default_branch_when_that_branch_exists()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp, mainBranch: "trunk");
        await repo.GitAsync("branch", "main");
        await repo.GitAsync("config", "init.defaultBranch", "trunk");

        Assert.Equal("trunk", (await SnapshotAsync(temp, repo.Path)).MainBranch);
    }

    [Fact]
    public async Task Main_ignores_init_default_branch_when_that_branch_is_missing()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("config", "init.defaultBranch", "trunk");

        Assert.Equal("main", (await SnapshotAsync(temp, repo.Path)).MainBranch);
    }

    [Fact]
    public async Task Main_falls_back_to_master()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp, mainBranch: "master");

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Equal("master", snapshot.MainBranch);
        Assert.True(Assert.Single(snapshot.Branches).IsMergedIntoMain);
    }

    [Fact]
    public async Task Main_from_options_wins()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("branch", "release");

        Assert.Equal("release", (await SnapshotAsync(temp, repo.Path, new SnapshotOptions { MainBranch = "release" })).MainBranch);
    }

    // --- merged and commits at risk ---------------------------------------------

    [Fact]
    public async Task Unmerged_branch_counts_its_unique_commits()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.CommitAsync("b.txt", "b", "B");
        await repo.GitAsync("switch", "-q", "main");

        var branch = Assert.Single((await SnapshotAsync(temp, repo.Path)).Branches, b => b.Name == "feature");

        Assert.False(branch.IsMergedIntoMain);
        Assert.Equal(2, branch.CommitsAtRisk);
    }

    [Fact]
    public async Task Merged_branch_has_nothing_at_risk()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("merge", "-q", "--ff-only", "feature");

        var branch = Assert.Single((await SnapshotAsync(temp, repo.Path)).Branches, b => b.Name == "feature");

        Assert.True(branch.IsMergedIntoMain);
        Assert.Equal(0, branch.CommitsAtRisk);
    }

    [Fact]
    public async Task Commits_on_another_branch_or_tag_are_not_at_risk()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.GitAsync("branch", "copy-of-feature");
        await repo.CommitAsync("b.txt", "b", "B");
        await repo.GitAsync("tag", "keep-b");
        await repo.GitAsync("switch", "-q", "main");

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Equal(0, snapshot.Branches.Single(b => b.Name == "feature").CommitsAtRisk);
        Assert.Equal(0, snapshot.Branches.Single(b => b.Name == "copy-of-feature").CommitsAtRisk);
        Assert.Equal(0, snapshot.Tags.Single(t => t.Name == "keep-b").CommitsAtRisk);
    }

    [Fact]
    public async Task Worktree_heads_and_reflogs_do_not_protect_commits()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        // Made with switch and commit, so every commit is also in the HEAD reflog.
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.CommitAsync("b.txt", "b", "B");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("worktree", "add", "-q", "--detach", temp.Combine("wt"), "feature");

        var branch = Assert.Single((await SnapshotAsync(temp, repo.Path)).Branches, b => b.Name == "feature");

        Assert.Equal(2, branch.CommitsAtRisk);
    }

    [Fact]
    public async Task Annotated_tag_with_unique_commits_is_at_risk()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("switch", "-q", "-c", "doomed");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.GitAsync("tag", "-a", "archive/doomed", "-m", "Backup");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("branch", "-q", "-D", "doomed");

        var tag = Assert.Single((await SnapshotAsync(temp, repo.Path)).Tags);

        Assert.Equal(("archive/doomed", false, 1), (tag.Name, tag.IsMergedIntoMain, tag.CommitsAtRisk));
        Assert.Equal(Jan15, tag.CreatedAt);
    }

    [Fact]
    public async Task Squash_merged_branch_has_gone_upstream_and_keeps_its_count()
    {
        using var temp = new TempDirectory();
        var origin = await FixtureRepo.InitAsync(temp, "origin.git", bare: true);
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "-u", "origin", "main");
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.CommitAsync("b.txt", "b", "B");
        await repo.GitAsync("push", "-q", "-u", "origin", "feature");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("merge", "-q", "--squash", "feature");
        await repo.GitAsync("commit", "-q", "-m", "Feature (squashed)");
        await repo.GitAsync("push", "-q", "origin", "main", ":feature");
        await repo.GitAsync("fetch", "-q", "--prune");

        var branch = Assert.Single((await SnapshotAsync(temp, repo.Path)).Branches, b => b.Name == "feature");

        Assert.Equal(("origin/feature", true, false, 2), (branch.Upstream, branch.IsUpstreamGone, branch.IsMergedIntoMain, branch.CommitsAtRisk));
    }

    [Fact]
    public async Task Merged_into_origin_main_counts_even_when_local_main_is_behind()
    {
        using var temp = new TempDirectory();
        var origin = await FixtureRepo.InitAsync(temp, "origin.git", bare: true);
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "-u", "origin", "main");
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.GitAsync("push", "-q", "origin", "feature:main");
        await repo.GitAsync("switch", "-q", "main");

        var snapshot = await SnapshotAsync(temp, repo.Path);

        var feature = snapshot.Branches.Single(b => b.Name == "feature");
        Assert.True(feature.IsMergedIntoMain);
        Assert.Equal(0, feature.CommitsAtRisk);
    }

    [Fact]
    public async Task Remote_branches_get_a_risk_count_only_when_asked()
    {
        using var temp = new TempDirectory();
        var origin = await FixtureRepo.InitAsync(temp, "origin.git", bare: true);
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "-u", "origin", "main");
        await repo.GitAsync("switch", "-q", "-c", "copilot/remote-only");
        await repo.CommitAsync("a.txt", "a", "A");
        await repo.GitAsync("push", "-q", "origin", "copilot/remote-only");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("branch", "-q", "-D", "copilot/remote-only");

        var byDefault = await SnapshotAsync(temp, repo.Path);
        var asked = await SnapshotAsync(temp, repo.Path, new SnapshotOptions { AnalyzeRisk = _ => true });

        var remote = byDefault.Branches.Single(b => b.Name == "origin/copilot/remote-only");
        Assert.True(remote.IsRemote);
        Assert.Null(remote.CommitsAtRisk);
        Assert.Equal(1, asked.Branches.Single(b => b.Name == "origin/copilot/remote-only").CommitsAtRisk);
    }

    [Fact]
    public async Task Remote_head_symref_is_not_a_branch()
    {
        using var temp = new TempDirectory();
        var origin = await FixtureRepo.InitAsync(temp, "origin.git", bare: true);
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "-u", "origin", "main");
        await repo.GitAsync("remote", "set-head", "origin", "main");

        var names = (await SnapshotAsync(temp, repo.Path)).Branches.Select(b => b.Name);

        Assert.Equal(["main", "origin/main"], names);
    }

    // --- worktrees --------------------------------------------------------------

    [Fact]
    public async Task Linked_worktree_status_and_ahead_behind_main()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature", temp.Combine("wt"));
        var wt = repo.At(temp.Combine("wt"));
        await wt.CommitAsync("feature.txt", "f", "Feature work");
        await repo.CommitAsync("main.txt", "m", "Main moves on");
        wt.WriteFile("feature.txt", "changed");
        wt.WriteFile("new file.txt", "untracked");

        var info = Assert.Single((await SnapshotAsync(temp, repo.Path)).Worktrees, w => !w.IsMain);

        Assert.Equal(Full(temp.Combine("wt")), info.Path);
        Assert.Equal(("feature", 2, 1, 1), (info.Branch, info.DirtyFileCount, info.AheadOfMain, info.BehindMain));
    }

    [Fact]
    public async Task Last_activity_is_the_newest_changed_file()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        var fileTime = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        repo.WriteFile("readme.md", "edited", fileTime);
        repo.WriteFile("older.txt", "untracked", fileTime.AddDays(-10));
        SetIndexTime(repo.Path, new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));

        var main = Assert.Single((await SnapshotAsync(temp, repo.Path)).Worktrees);

        Assert.Equal(Jan15, main.LastCommitAt);
        Assert.Equal(fileTime, main.LastActivityAt);
    }

    [Fact]
    public async Task Last_activity_is_the_index_when_it_is_newest()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature", temp.Combine("wt"));
        var indexTime = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        SetIndexTime(temp.Combine("wt"), indexTime);

        var linked = Assert.Single((await SnapshotAsync(temp, repo.Path)).Worktrees, w => !w.IsMain);

        Assert.Equal(indexTime, linked.LastActivityAt);
    }

    [Fact]
    public async Task Last_activity_is_the_last_commit_when_files_are_older()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        SetIndexTime(repo.Path, Jan15.AddDays(-5));

        var main = Assert.Single((await SnapshotAsync(temp, repo.Path)).Worktrees);

        Assert.Equal(Jan15, main.LastActivityAt);
    }

    [Fact]
    public async Task Prunable_locked_and_detached_worktrees()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("worktree", "add", "-q", "-b", "gone", temp.Combine("wt-gone"));
        Directory.Delete(temp.Combine("wt-gone"), recursive: true);
        await repo.GitAsync("worktree", "add", "-q", "-b", "locked", temp.Combine("wt-locked"));
        await repo.GitAsync("worktree", "lock", "--reason", "agent busy", temp.Combine("wt-locked"));
        await repo.GitAsync("worktree", "add", "-q", "--detach", temp.Combine("wt-detached"));

        var worktrees = (await SnapshotAsync(temp, repo.Path)).Worktrees;

        var gone = worktrees.Single(w => w.Branch == "gone");
        Assert.True(gone.IsPrunable);
        Assert.Null(gone.DirtyFileCount);
        Assert.Equal(gone.LastCommitAt, gone.LastActivityAt);
        var locked = worktrees.Single(w => w.Branch == "locked");
        Assert.Equal((true, "agent busy"), (locked.IsLocked, locked.LockReason));
        var detached = worktrees.Single(w => w.Path == Full(temp.Combine("wt-detached")));
        Assert.True(detached.IsDetached);
        Assert.Null(detached.Branch);
        Assert.Equal(0, detached.DirtyFileCount);
    }

    [Fact]
    public async Task Bare_repo_with_a_linked_worktree()
    {
        using var temp = new TempDirectory();
        var source = await RepoWithCommitAsync(temp, "source");
        await source.GitAsync("clone", "-q", "--bare", source.Path, temp.Combine("bare.git"));
        var bare = source.At(temp.Combine("bare.git"));
        await bare.GitAsync("worktree", "add", "-q", temp.Combine("wt"), "main");

        var snapshot = await SnapshotAsync(temp, bare.Path);

        Assert.Equal(Full(bare.Path), snapshot.RepoRoot);
        Assert.True(snapshot.Worktrees[0].IsBare);
        Assert.Null(snapshot.Worktrees[0].DirtyFileCount);
        Assert.Equal(0, snapshot.Worktrees[1].DirtyFileCount);
    }

    // --- stashes, unreachable and other refs ------------------------------------

    [Fact]
    public async Task Stashes_and_orphaned_commits_are_kept_apart()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        repo.WriteFile("readme.md", "one");
        await repo.GitAsync("stash", "push", "-q", "-m", "first");
        repo.WriteFile("readme.md", "two");
        await repo.GitAsync("stash", "push", "-q", "-m", "autostash");
        var orphan = await CommitTreeAsync(repo);

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Equal(["On main: autostash", "On main: first"], snapshot.Stashes.Select(s => s.Message));
        Assert.Equal([orphan], snapshot.UnreachableCommits.Select(c => c.Id));
    }

    [Fact]
    public async Task Commits_still_in_a_reflog_are_not_unreachable()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.CommitAsync("reset.txt", "r", "Reset away");
        await repo.GitAsync("reset", "-q", "--hard", "HEAD~1");
        await repo.GitAsync("switch", "-q", "-c", "squashed");
        await repo.CommitAsync("squashed.txt", "s", "Squash-merged elsewhere");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("branch", "-q", "-D", "squashed");

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Empty(snapshot.UnreachableCommits);
    }

    [Fact]
    public async Task Commits_only_in_a_linked_worktree_reflog_are_not_unreachable()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        var path = temp.Combine("linked");
        await repo.GitAsync("worktree", "add", "-q", "--detach", path);
        var worktree = repo.At(path);
        await worktree.CommitAsync("detached.txt", "d", "Detached work");
        await worktree.GitAsync("checkout", "-q", "--detach", "main");

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Empty(snapshot.UnreachableCommits);
    }

    [Fact]
    public async Task Unreachable_commits_are_sorted_by_id()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        var baseline = await CommitTreeAsync(repo);
        var child = await CommitTreeAsync(repo, parent: baseline);

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Equal(new[] { baseline, child }.Order(StringComparer.Ordinal), snapshot.UnreachableCommits.Select(c => c.Id));
    }

    [Fact]
    public async Task Unreachable_commits_have_subject_and_date()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        repo.Clock.Now = Jan15.AddDays(2);
        var orphan = await CommitTreeAsync(repo, "Agent host session x - baseline checkpoint");

        var snapshot = await SnapshotAsync(temp, repo.Path);

        Assert.Equal(new UnreachableCommit(orphan, Jan15.AddDays(2), "Agent host session x - baseline checkpoint"), Assert.Single(snapshot.UnreachableCommits));
    }

    [Fact]
    public async Task Unreachable_commits_can_be_skipped()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await CommitTreeAsync(repo);

        var result = await BuildAsync(temp, repo.Path, new SnapshotOptions { IncludeUnreachable = false });

        Assert.Empty(result.Snapshot.UnreachableCommits);
        Assert.DoesNotContain(result.Timings, t => t.Command.StartsWith("git fsck", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checkpoint_refs_are_other_refs_with_commit_dates()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        repo.Clock.Now = Jan15.AddDays(3);
        var checkpoint = await repo.CommitAsync("cp.txt", "cp", "Checkpoint");
        await repo.GitAsync("update-ref", "refs/copilot/checkpoints/session-1/1/cp-a", checkpoint);
        await repo.GitAsync("reset", "-q", "--hard", "HEAD~1");
        repo.WriteFile("readme.md", "stashed");
        await repo.GitAsync("stash", "push", "-q");

        var snapshot = await SnapshotAsync(temp, repo.Path);

        var other = Assert.Single(snapshot.OtherRefs);
        Assert.Equal(("refs/copilot/checkpoints/session-1/1/cp-a", checkpoint, Jan15.AddDays(3)), (other.FullName, other.TargetId, other.CreatedAt));
        Assert.Empty(snapshot.UnreachableCommits);
    }

    // --- leftover folders -------------------------------------------------------

    [Fact]
    public async Task Leftover_folder_next_to_a_worktree_in_a_container()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        var container = temp.Combine("copilot-worktrees", "repo");
        await repo.GitAsync("worktree", "add", "-q", "-b", "me-curly-waddle", Path.Combine(container, "me-curly-waddle"));
        Directory.CreateDirectory(Path.Combine(container, "me-left-behind", "src"));
        File.WriteAllText(Path.Combine(container, "me-left-behind", "src", "file.cs"), "x");
        Directory.CreateDirectory(Path.Combine(container, "someone-elses-checkout"));
        File.WriteAllText(Path.Combine(container, "someone-elses-checkout", ".git"), "gitdir: elsewhere");

        var snapshot = await SnapshotAsync(temp, repo.Path, new SnapshotOptions { WorktreeContainerPatterns = ["**/copilot-worktrees/*"] });

        Assert.Equal([Full(Path.Combine(container, "me-left-behind"))], snapshot.LeftoverFolders);
    }

    [Fact]
    public async Task Folders_outside_containers_are_never_listed()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature", temp.Combine("projects", "feature"));
        Directory.CreateDirectory(temp.Combine("projects", "unrelated-project"));

        var withPatterns = await SnapshotAsync(temp, repo.Path, new SnapshotOptions { WorktreeContainerPatterns = ["**/copilot-worktrees/*"] });
        var withoutPatterns = await SnapshotAsync(temp, repo.Path);

        Assert.Empty(withPatterns.LeftoverFolders);
        Assert.Empty(withoutPatterns.LeftoverFolders);
    }

    [Fact]
    public async Task Leftover_folder_in_claude_worktrees_inside_the_repo()
    {
        using var temp = new TempDirectory();
        var repo = await RepoWithCommitAsync(temp);
        Directory.CreateDirectory(Path.Combine(repo.Path, ".claude", "worktrees", "agent-1"));

        var snapshot = await SnapshotAsync(temp, repo.Path, new SnapshotOptions { WorktreeContainerPatterns = ["**/.claude/worktrees"] });

        Assert.Equal([Full(Path.Combine(repo.Path, ".claude", "worktrees", "agent-1"))], snapshot.LeftoverFolders);
    }

    private static void SetIndexTime(string worktreePath, DateTimeOffset time) =>
        File.SetLastWriteTimeUtc(GitDirectory.FindIndexFile(worktreePath)!, time.UtcDateTime);
}
