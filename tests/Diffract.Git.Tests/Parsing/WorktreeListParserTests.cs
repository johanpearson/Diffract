using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Parsing;

public class WorktreeListParserTests
{
    private static WorktreeEntry ByPathEnd(IReadOnlyList<WorktreeEntry> all, string end) =>
        Assert.Single(all, w => w.Path.EndsWith(end, StringComparison.Ordinal));

    [Fact]
    public void Single_main_worktree()
    {
        var main = Assert.Single(WorktreeListParser.Parse(Sample.Read("worktree-list-single.bin")));

        Assert.EndsWith("/repo", main.Path);
        Assert.Equal("refs/heads/main", main.Branch);
        Assert.Equal(40, main.Head?.Length);
        Assert.False(main.IsBare || main.IsDetached || main.IsLocked || main.IsPrunable);
    }

    [Fact]
    public void Main_worktree_comes_first_with_all_linked_worktrees_after()
    {
        var all = WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin"));

        Assert.Equal(8, all.Count);
        Assert.EndsWith("/repo", all[0].Path);
    }

    [Fact]
    public void Same_list_when_run_from_a_linked_worktree()
    {
        Assert.Equal(
            WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")),
            WorktreeListParser.Parse(Sample.Read("worktree-list-from-linked.bin")));
    }

    [Fact]
    public void Path_with_spaces()
    {
        var wt = ByPathEnd(WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")), "/wt with spaces");

        Assert.Equal("refs/heads/feature/one", wt.Branch);
    }

    [Fact]
    public void Path_and_branch_with_swedish_letters()
    {
        var wt = ByPathEnd(WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")), "/wt-åäö");

        Assert.Equal("refs/heads/feature/åäö", wt.Branch);
    }

    [Fact]
    public void Very_long_path()
    {
        var wt = ByPathEnd(WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")), "/worktree");

        Assert.True(wt.Path.Length >= 200, $"Sample path is only {wt.Path.Length} characters");
        Assert.Equal("refs/heads/long-path", wt.Branch);
    }

    [Fact]
    public void Detached_head()
    {
        var wt = ByPathEnd(WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")), "/wt-detached");

        Assert.True(wt.IsDetached);
        Assert.Null(wt.Branch);
        Assert.NotNull(wt.Head);
    }

    [Fact]
    public void Locked_without_reason()
    {
        var wt = ByPathEnd(WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")), "/wt-locked-plain");

        Assert.True(wt.IsLocked);
        Assert.Null(wt.LockReason);
    }

    [Fact]
    public void Locked_with_reason()
    {
        var wt = ByPathEnd(WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")), "/wt-locked-reason");

        Assert.True(wt.IsLocked);
        Assert.Equal("Claude Code session 42", wt.LockReason);
    }

    [Fact]
    public void Prunable_with_reason()
    {
        var wt = ByPathEnd(WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin")), "/wt-prunable");

        Assert.True(wt.IsPrunable);
        Assert.Equal("gitdir file points to non-existent location", wt.PrunableReason);
        Assert.Equal("refs/heads/gone-folder", wt.Branch);
    }

    [Fact]
    public void Only_flagged_worktrees_are_locked_or_prunable()
    {
        var all = WorktreeListParser.Parse(Sample.Read("worktree-list-many.bin"));

        Assert.Equal(2, all.Count(w => w.IsLocked));
        Assert.Single(all, w => w.IsPrunable);
    }

    [Fact]
    public void Bare_repo_with_a_linked_worktree()
    {
        var all = WorktreeListParser.Parse(Sample.Read("worktree-list-bare.bin"));

        Assert.Equal(2, all.Count);
        Assert.True(all[0].IsBare);
        Assert.Null(all[0].Head);
        Assert.EndsWith("/bare.git", all[0].Path);
        Assert.False(all[1].IsBare);
        Assert.Equal("refs/heads/main", all[1].Branch);
    }

    [Fact]
    public void Empty_output_means_no_worktrees()
    {
        Assert.Empty(WorktreeListParser.Parse(""));
    }

    [Fact]
    public void Ignores_attributes_from_newer_git_versions()
    {
        var wt = Assert.Single(WorktreeListParser.Parse("worktree C:/x\0HEAD abc\0branch refs/heads/x\0future-flag value\0\0"));

        Assert.Equal("refs/heads/x", wt.Branch);
    }
}
