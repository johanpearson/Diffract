using Diffract.Fixtures;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Snapshot;

/// <summary>
/// The quick snapshot a repo opens with: what history needs, in 4 git calls whatever the repo holds. The full
/// snapshot fills in the rest afterwards, so the quick one must agree with it on everything it does have.
/// </summary>
public sealed class QuickSnapshotTests : IDisposable
{
    private static readonly SnapshotOptions Options = new() { WorktreeContainerPatterns = ["**/copilot-worktrees/*", "**/.claude/worktrees"] };

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RepoSnapshotBuilder Builder() => new(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }, Options);

    public static TheoryData<string> Scenarios => [.. FixtureRepoBuilder.ScenarioNames];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Takes_four_git_calls_and_never_fsck_or_status(string scenario)
    {
        var built = await new FixtureRepoBuilder(_temp).BuildAsync(scenario);

        var result = await Builder().BuildQuickAsync(built.RepoPath, Ct);

        var calls = result.Timings.Select(t => t.Command.Split(' ')[1]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["config", "for-each-ref", "version", "worktree"], calls);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Agrees_with_the_full_snapshot_on_refs_worktrees_and_main(string scenario)
    {
        var built = await new FixtureRepoBuilder(_temp).BuildAsync(scenario);

        var quick = (await Builder().BuildQuickAsync(built.RepoPath, Ct)).Snapshot;
        var full = (await Builder().BuildAsync(built.RepoPath, Ct)).Snapshot;

        Assert.Equal(full.RepoRoot, quick.RepoRoot);
        Assert.Equal(full.MainBranch, quick.MainBranch);
        Assert.Equal(full.Branches.Select(b => (b.FullName, b.TargetId)), quick.Branches.Select(b => (b.FullName, b.TargetId)));
        Assert.Equal(full.Tags.Select(t => (t.FullName, t.TargetId)), quick.Tags.Select(t => (t.FullName, t.TargetId)));
        Assert.Equal(full.OtherRefs, quick.OtherRefs);
        Assert.Equal(
            full.Worktrees.Select(w => (w.Path, w.IsMain, w.Head, w.Branch, w.IsDetached, w.IsLocked, w.IsPrunable)),
            quick.Worktrees.Select(w => (w.Path, w.IsMain, w.Head, w.Branch, w.IsDetached, w.IsLocked, w.IsPrunable)));
    }

    [Fact]
    public async Task Leaves_what_the_scan_finds_empty()
    {
        var built = await new FixtureRepoBuilder(_temp).StaleAgentWorktreeAsync();

        var quick = (await Builder().BuildQuickAsync(built.RepoPath, Ct)).Snapshot;

        Assert.All(quick.Worktrees, w => Assert.Null(w.DirtyFileCount));
        Assert.All(quick.Branches, b => Assert.Null(b.CommitsAtRisk));
        Assert.Empty(quick.Stashes);
        Assert.Empty(quick.UnreachableCommits);
        Assert.Empty(quick.LeftoverFolders);
    }

    [Fact]
    public async Task Says_plainly_when_the_folder_is_not_a_repo()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temp.Path, "not a repo")).FullName;

        await Assert.ThrowsAsync<NotAGitRepositoryException>(() => Builder().BuildQuickAsync(folder, Ct));
    }
}
