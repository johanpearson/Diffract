using Diffract.Git.Snapshot;
using Diffract.Git.Watching;

namespace Diffract.Git.Tests.Watching;

/// <summary>Which change each path means. No files and no git: the classifier only looks at paths.</summary>
public class ChangeClassifierTests
{
    private const string Main = @"C:\src\shop";
    private const string Git = @"C:\src\shop\.git";
    private const string Nested = @"C:\src\shop\.claude\worktrees\räksmörgås fix";
    private const string Sibling = @"C:\src\shop-hotfix";

    private static readonly WatchLayout Layout = new(
        Git,
        [
            new WatchedWorktree(Main, Git, HasFiles: true),
            new WatchedWorktree(Nested, @"C:\src\shop\.git\worktrees\räksmörgås fix", HasFiles: true),
            new WatchedWorktree(Sibling, @"C:\src\shop\.git\worktrees\shop-hotfix", HasFiles: true),
        ],
        [@"C:\src\shop\.claude\worktrees", @"C:\src"]);

    public static TheoryData<string, string> Paths => new()
    {
        // The common git directory.
        { @"C:\src\shop\.git\HEAD", $"HEAD of {Main}" },
        { @"C:\src\shop\.git\index", $"HEAD of {Main}" },
        { @"C:\src\shop\.git\packed-refs", "refs" },
        { @"C:\src\shop\.git\refs\heads\claude\fix-login", "refs" },
        { @"C:\src\shop\.git\refs\copilot\checkpoints\abc\0001", "refs" },
        { @"C:\src\shop\.git\refs\stash", "refs" },
        { @"C:\src\shop\.git\worktrees\räksmörgås fix\HEAD", $"HEAD of {Nested}" },
        { @"C:\src\shop\.git\worktrees\shop-hotfix\index", $"HEAD of {Sibling}" },
        { @"C:\src\shop\.git\worktrees\räksmörgås fix\locked", "worktree list" },
        { @"C:\src\shop\.git\worktrees\brand-new", "worktree list" },
        { @"C:\src\shop\.git\worktrees\brand-new\HEAD", "worktree list" },
        { @"C:\src\shop\.git\worktrees", "worktree list" },

        // Git's own bookkeeping, and the lock files it renames into place.
        { @"C:\src\shop\.git\index.lock", "nothing" },
        { @"C:\src\shop\.git\refs\heads\main.lock", "nothing" },
        { @"C:\src\shop\.git\objects\ab\cdef0123", "nothing" },
        { @"C:\src\shop\.git\logs\HEAD", "nothing" },
        { @"C:\src\shop\.git\FETCH_HEAD", "nothing" },
        { @"C:\src\shop\.git\worktrees\shop-hotfix\logs\HEAD", "nothing" },
        { @"C:\src\shop\.git", "nothing" },

        // Worktree files: the deepest worktree wins.
        { @"C:\src\shop\src\cart.cs", $"files in {Main}" },
        { @"C:\src\shop\.claude\worktrees\räksmörgås fix\src\cart.cs", $"files in {Nested}" },
        { @"C:\src\shop-hotfix\README.md", $"files in {Sibling}" },
        { @"C:\src\shop\.claude\worktrees\räksmörgås fix\.git", "nothing" },

        // Containers: a folder appearing or going away.
        { @"C:\src\shop\.claude\worktrees\old-task", "worktree list" },
        { @"C:\src\shop-experiment", "worktree list" },

        // Somewhere else.
        { @"D:\elsewhere\file.txt", "nothing" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void Classifies_the_path(string path, string expected)
    {
        Assert.Equal(expected, ChangeClassifier.Classify(path, Layout).ToString());
    }

    [Fact]
    public void Paths_compare_ignoring_case_and_trailing_separators()
    {
        Assert.Equal("refs", ChangeClassifier.Classify(@"c:\SRC\Shop\.GIT\packed-refs", Layout).ToString());
        Assert.Equal($"files in {Sibling}", ChangeClassifier.Classify(@"C:\src\shop-hotfix\docs\", Layout).ToString());
    }

    [Fact]
    public void A_bare_repo_head_is_a_head_change_of_the_bare_repo()
    {
        var bare = new WatchLayout(@"C:\src\shop.git", [new WatchedWorktree(@"C:\src\shop.git", @"C:\src\shop.git", HasFiles: false)], []);

        Assert.Equal(@"HEAD of C:\src\shop.git", ChangeClassifier.Classify(@"C:\src\shop.git\HEAD", bare).ToString());
        Assert.Equal("refs", ChangeClassifier.Classify(@"C:\src\shop.git\refs\heads\main", bare).ToString());
    }

    [Fact]
    public void A_worktree_whose_folder_is_gone_has_no_file_changes()
    {
        var layout = Layout with { Worktrees = [new WatchedWorktree(Main, Git, true), new WatchedWorktree(Sibling, null, HasFiles: false)] };

        Assert.True(ChangeClassifier.Classify(@"C:\src\shop-hotfix\README.md", layout).IsEmpty);
    }

    [Fact]
    public void Watches_each_folder_once_and_containers_inside_a_worktree_not_at_all()
    {
        Assert.Equal(
            [(Main, true), (Sibling, true), (@"C:\src", false)],
            Layout.Roots().OrderBy(r => r.Recursive ? 0 : 1).ThenBy(r => r.Path, StringComparer.Ordinal));
    }

    [Fact]
    public void Merging_changes_keeps_every_part()
    {
        var merged = ChangeSet.FilesChanged(Main)
            .Merge(ChangeSet.HeadChanged(Sibling))
            .Merge(ChangeSet.RefsChanged)
            .Merge(ChangeSet.FilesChanged(Main.ToUpperInvariant()));

        Assert.Equal($"refs, HEAD of {Sibling}, files in {Main}", merged.ToString());
        Assert.False(merged.IsEmpty);
        Assert.True(ChangeSet.None.Merge(ChangeSet.None).IsEmpty);
    }
}
