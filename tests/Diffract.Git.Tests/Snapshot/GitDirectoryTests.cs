using Diffract.Fixtures;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Snapshot;

public class GitDirectoryTests
{
    [Theory]
    [InlineData(@"C:\src\repo\.git\worktrees\name", @"C:\src\repo")]
    [InlineData(@"C:\src\repo\.git\worktrees\name\", @"C:\src\repo")]
    [InlineData(@"C:\src\repo.git\worktrees\name", @"C:\src\repo.git")]
    [InlineData(@"C:\src\repo\.git\modules\name", null)]
    [InlineData(@"C:\src\repo\.git", null)]
    public void Finds_the_repo_a_worktree_record_belongs_to(string record, string? owner)
    {
        Assert.Equal(owner, GitDirectory.OwnerOfWorktreeRecord(record));
    }

    [Fact]
    public void Reads_a_relative_or_absolute_gitdir_link()
    {
        using var temp = new TempDirectory();
        var relative = Directory.CreateDirectory(temp.Combine("relative")).FullName;
        var absolute = Directory.CreateDirectory(temp.Combine("absolute")).FullName;
        File.WriteAllText(Path.Combine(relative, ".git"), "gitdir: ../repo/.git/worktrees/relative\n");
        File.WriteAllText(Path.Combine(absolute, ".git"), $"gitdir: {temp.Combine("repo", ".git", "worktrees", "absolute")}\n");

        Assert.Equal(temp.Combine("repo", ".git", "worktrees", "relative"), GitDirectory.ReadGitFileLink(relative));
        Assert.Equal(temp.Combine("repo", ".git", "worktrees", "absolute"), GitDirectory.ReadGitFileLink(absolute));
    }

    [Fact]
    public void No_link_without_a_gitdir_file()
    {
        using var temp = new TempDirectory();
        var plain = Directory.CreateDirectory(temp.Combine("plain")).FullName;
        var repo = Directory.CreateDirectory(temp.Combine("repo", ".git")).Parent!.FullName;
        var odd = Directory.CreateDirectory(temp.Combine("odd")).FullName;
        File.WriteAllText(Path.Combine(odd, ".git"), "not a link\n");

        Assert.Null(GitDirectory.ReadGitFileLink(plain));
        Assert.Null(GitDirectory.ReadGitFileLink(repo));
        Assert.Null(GitDirectory.ReadGitFileLink(odd));
    }
}
