namespace Diffract.Git.Tests;

public class ReadOnlyPolicyTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("rev-parse --show-toplevel")]
    [InlineData("worktree list --porcelain -z")]
    [InlineData("status --porcelain=v2 --branch -z")]
    [InlineData("for-each-ref --format=%(refname)")]
    [InlineData("stash list -z")]
    [InlineData("rev-list --count main..feature")]
    [InlineData("merge-base --is-ancestor feature main")]
    [InlineData("log --oneline -n 30 --all")]
    [InlineData("reflog")]
    [InlineData("reflog -n 50")]
    [InlineData("reflog show HEAD")]
    [InlineData("fsck --unreachable --no-progress")]
    [InlineData("config --get init.defaultBranch")]
    [InlineData("config --get-regexp ^remote\\.")]
    [InlineData("config --list")]
    [InlineData("config -l --show-origin")]
    [InlineData("config get user.name")]
    [InlineData("config list")]
    [InlineData("symbolic-ref HEAD")]
    [InlineData("symbolic-ref -q --short refs/remotes/origin/HEAD")]
    [InlineData("cat-file -t HEAD")]
    [InlineData("show-ref --verify refs/heads/main")]
    public void Allows_reads(string commandLine)
    {
        Assert.True(ReadOnlyPolicy.IsAllowed(commandLine.Split(' '), out var reason), reason);
    }

    [Theory]
    [InlineData("commit -m x")]
    [InlineData("push")]
    [InlineData("fetch")]
    [InlineData("pull")]
    [InlineData("checkout main")]
    [InlineData("switch main")]
    [InlineData("reset --hard HEAD~1")]
    [InlineData("branch -D feature")]
    [InlineData("tag archive/x")]
    [InlineData("update-ref -d refs/copilot/checkpoints/a")]
    [InlineData("gc")]
    [InlineData("prune")]
    [InlineData("stash")]
    [InlineData("stash push")]
    [InlineData("stash drop")]
    [InlineData("stash clear")]
    [InlineData("worktree add ../x")]
    [InlineData("worktree remove ../x")]
    [InlineData("worktree prune")]
    [InlineData("worktree lock ../x")]
    [InlineData("reflog expire --all")]
    [InlineData("reflog delete HEAD@{1}")]
    [InlineData("config user.name Someone")]
    [InlineData("config --unset user.name")]
    [InlineData("config --get user.name --add")]
    [InlineData("config set user.name Someone")]
    [InlineData("config --edit")]
    [InlineData("symbolic-ref HEAD refs/heads/other")]
    [InlineData("symbolic-ref -d HEAD")]
    [InlineData("symbolic-ref -m reason HEAD")]
    [InlineData("log --output=out.txt")]
    [InlineData("log --output out.txt")]
    [InlineData("fsck --lost-found")]
    [InlineData("-c core.editor=x status")]
    [InlineData("--git-dir=. status")]
    public void Rejects_anything_that_can_write(string commandLine)
    {
        Assert.False(ReadOnlyPolicy.IsAllowed(commandLine.Split(' '), out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Rejects_empty_command()
    {
        Assert.False(ReadOnlyPolicy.IsAllowed([], out _));
    }
}
