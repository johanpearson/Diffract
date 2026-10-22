namespace Diffract.Git.Tests;

public class ChangesPolicyTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>One line per allowed command, in the order of <see cref="ChangesPolicy.AllowedShapes"/>.</summary>
    public static TheoryData<string> AllowedCommands =>
    [
        "add --pathspec-from-file=- --pathspec-file-nul",
        "add --intent-to-add -- new-file.txt",
        "restore --staged --pathspec-from-file=- --pathspec-file-nul",
        "rm --cached -q --pathspec-from-file=- --pathspec-file-nul",
        "apply --cached --unidiff-zero -",
        "apply --cached --unidiff-zero --reverse -",
        "commit -F -",
        "commit --amend -F -",
        "fetch origin",
        "merge --ff-only origin/main",
        "push --porcelain origin feature/åäö",
        "push --porcelain --set-upstream origin feature/x",
        $"branch feature/x {Commit}",
        "switch main",
        $"switch -c feature/x {Commit}",
        "switch --track origin/feature/x",
        $"switch --detach {Commit}",
        $"tag -a v1.0 {Commit} -F -",
        $"reset --soft {Commit}",
        $"reset --mixed {Commit}",
    ];

    [Theory]
    [MemberData(nameof(AllowedCommands))]
    public void Allows_each_command_on_the_list(string commandLine)
    {
        Assert.True(ChangesPolicy.IsAllowed(Split(commandLine), out var reason), reason);
    }

    [Fact]
    public void Every_shape_on_the_list_has_a_test()
    {
        Assert.Equal(ChangesPolicy.AllowedShapes.Count, AllowedCommands.Count);
    }

    [Fact]
    public void A_path_after_the_double_dash_may_have_spaces_or_look_like_an_option()
    {
        Assert.True(ChangesPolicy.IsAllowed(["add", "--intent-to-add", "--", "new file åäö.txt"], out _));
        Assert.True(ChangesPolicy.IsAllowed(["add", "--intent-to-add", "--", "--force"], out _));
    }

    [Theory]
    // Anything that forces, skips hooks or throws work away.
    [InlineData("push --force origin main")]
    [InlineData("push --porcelain --force origin main")]
    [InlineData("push --porcelain --force-with-lease origin main")]
    [InlineData("push --porcelain origin +main")]
    [InlineData("push --porcelain origin :main")]
    [InlineData("push --porcelain origin main:other")]
    [InlineData("push --porcelain --delete origin main")]
    [InlineData("push origin main")]
    [InlineData("commit --no-verify -F -")]
    [InlineData("commit -n -F -")]
    [InlineData("commit -m message")]
    [InlineData("commit --allow-empty -F -")]
    [InlineData($"reset --hard {Commit}")]
    [InlineData($"reset --merge {Commit}")]
    [InlineData($"reset --keep {Commit}")]
    [InlineData("reset --soft HEAD~1")]
    [InlineData("reset --mixed")]
    [InlineData("reset")]
    [InlineData("checkout main")]
    [InlineData("checkout -- file.txt")]
    [InlineData("clean -fd")]
    [InlineData("stash")]
    [InlineData("stash push")]
    [InlineData("restore file.txt")]
    [InlineData("restore --staged --worktree --pathspec-from-file=- --pathspec-file-nul")]
    [InlineData("restore --worktree --pathspec-from-file=- --pathspec-file-nul")]
    [InlineData("rebase main")]
    [InlineData("merge main")]
    [InlineData("merge --ff-only -s ours main")]
    [InlineData("merge --ff-only --no-ff main")]
    [InlineData("pull")]
    [InlineData("pull --ff-only")]
    [InlineData("fetch --prune origin")]
    [InlineData("fetch -p origin")]
    [InlineData("fetch")]
    [InlineData("switch --discard-changes main")]
    [InlineData("switch -f main")]
    [InlineData("switch --force main")]
    [InlineData("switch -C main 0123456789abcdef0123456789abcdef01234567")]
    [InlineData("switch -c feature HEAD")]
    [InlineData("branch -D feature")]
    [InlineData("branch -f main 0123456789abcdef0123456789abcdef01234567")]
    [InlineData("branch -d feature")]
    [InlineData("tag -d v1.0")]
    [InlineData("tag -f -a v1.0 0123456789abcdef0123456789abcdef01234567 -F -")]
    [InlineData("rm file.txt")]
    [InlineData("rm -q --pathspec-from-file=- --pathspec-file-nul")]
    // Staging more than asked for.
    [InlineData("add -A")]
    [InlineData("add .")]
    [InlineData("add --all")]
    [InlineData("add -f --pathspec-from-file=- --pathspec-file-nul")]
    [InlineData("add --intent-to-add file.txt")]
    [InlineData("apply -")]
    [InlineData("apply --cached -")]
    [InlineData("apply --unidiff-zero -")]
    [InlineData("apply --cached --unidiff-zero patch.diff")]
    // Options before the subcommand, and config from the command line.
    [InlineData("-c core.hooksPath=/tmp/x commit -F -")]
    [InlineData("--git-dir=/elsewhere add --pathspec-from-file=- --pathspec-file-nul")]
    // Reads go through the read-only runner, never the changes runner.
    [InlineData("status --porcelain=v2 -z")]
    [InlineData("diff -U0")]
    [InlineData("cat-file blob 0123456789abcdef0123456789abcdef01234567")]
    [InlineData("")]
    public void Rejects_anything_else(string commandLine)
    {
        Assert.False(ChangesPolicy.IsAllowed(Split(commandLine), out var reason));
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("-origin")]
    [InlineData("+main")]
    [InlineData("a:b")]
    [InlineData("two words")]
    [InlineData("")]
    public void A_name_that_git_could_read_as_an_option_a_force_or_a_refspec_is_rejected(string name)
    {
        Assert.False(ChangesPolicy.IsAllowed(["switch", name], out _));
        Assert.False(ChangesPolicy.IsAllowed(["fetch", name], out _));
    }

    private static string[] Split(string commandLine) =>
        commandLine.Length == 0 ? [] : commandLine.Split(' ');
}
