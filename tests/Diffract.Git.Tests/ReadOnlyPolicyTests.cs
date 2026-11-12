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
    [InlineData("diff-tree -r -z --always --root --raw --numstat HEAD")]
    [InlineData("cat-file blob 0123456789abcdef0123456789abcdef01234567")]
    [InlineData("check-ref-format refs/heads/topic")]
    [InlineData("ls-remote --heads origin")]
    [InlineData("ls-remote --symref origin HEAD")]
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
    [InlineData("ls-remote origin")]
    [InlineData("ls-remote --heads")]
    [InlineData("ls-remote --heads origin extra")]
    [InlineData("ls-remote --heads https://example.com/repo.git")]
    [InlineData("ls-remote --heads --upload-pack=evil origin")]
    [InlineData("ls-remote --exit-code --heads origin")]
    [InlineData("ls-remote --symref origin")]
    [InlineData("ls-remote --symref origin refs/heads/main")]
    [InlineData("ls-remote --symref https://example.com/repo.git HEAD")]
    [InlineData("ls-remote --symref --upload-pack=evil origin HEAD")]
    [InlineData("push --delete origin main")]
    [InlineData("push origin --delete main")]
    [InlineData("symbolic-ref -m reason HEAD")]
    [InlineData("log --output=out.txt")]
    [InlineData("log --output out.txt")]
    [InlineData("diff-tree --output=out.txt HEAD")]
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
