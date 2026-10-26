using System.ComponentModel;
using Diffract.Fixtures;

namespace Diffract.Git.Tests;

public class CleanupPolicyTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Other = "89abcdef0123456789abcdef0123456789abcdef";
    private const string Folder = @"C:\src\shop\.claude\worktrees\räksmörgås fix";

    private static GitCommandLine Line(params string[] args) => new(args, []);

    private static GitCommandLine UpdateRef(params string[] input) => new(["update-ref", "--stdin"], input);

    private static bool Allowed(GitCommandLine command) => CleanupPolicy.IsAllowed(command, out _);

    /// <summary>One per allowed command, in the order of <see cref="CleanupPolicy.AllowedShapes"/>, with "|" between arguments.</summary>
    public static TheoryData<string> AllowedCommands =>
    [
        $"worktree|remove|{Folder}",
        $"worktree|add|{Folder}|claude/räksmörgås-fix",
        "branch|-D|claude/räksmörgås-fix",
        "branch|-d|-r|origin/done-1",
        $"branch|claude/räksmörgås-fix|{Commit}",
        $"tag|-a|archive/branch/claude/räksmörgås-fix-2026-10-26|{Commit}|-F|-",
        "tag|-d|archive/branch/old-2026-09-01",
        "stash|drop|stash@{12}",
        $"stash|store|-m|-starts with a dash|{Commit}",
        "update-ref|--stdin",
        "mktree",
        $"commit-tree|{EmptyTree}|-p|{Commit}|-p|{Other}|-F|-",
    ];

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    [Theory]
    [MemberData(nameof(AllowedCommands))]
    public void Allows_each_command_on_the_list(string commandLine)
    {
        var args = commandLine.Split('|');
        var command = args[0] == "update-ref" ? UpdateRef($"delete refs/copilot/checkpoints/abc/1 {Commit}") : Line(args);
        Assert.True(CleanupPolicy.IsAllowed(command, out var reason), reason);
    }

    [Fact]
    public void Every_shape_on_the_list_has_a_test()
    {
        Assert.Equal(CleanupPolicy.AllowedShapes.Count, AllowedCommands.Count);
    }

    [Theory]
    // Forcing, and anything that removes more than the step names.
    [InlineData("worktree", "remove", "--force", Folder)]
    [InlineData("worktree", "remove", "-f", Folder)]
    [InlineData("worktree", "remove", @"..\relative")]
    [InlineData("worktree", "prune")]
    [InlineData("worktree", "prune", "-v")]
    [InlineData("branch", "-D", "--force", "x")]
    [InlineData("branch", "-D", "-r", "origin/x")]
    [InlineData("branch", "-f", "main", Commit)]
    [InlineData("branch", "x", "HEAD~1")]
    [InlineData("tag", "-f", "-a", "v1", Commit, "-F", "-")]
    [InlineData("tag", "v1", Commit)]
    [InlineData("stash", "drop", Commit)]
    [InlineData("stash", "drop", "stash@{x}")]
    [InlineData("stash", "drop")]
    [InlineData("stash", "clear")]
    [InlineData("stash", "pop")]
    [InlineData("stash", "store", "-m", "", Commit)]
    [InlineData("update-ref", "-d", "refs/heads/main")]
    [InlineData("update-ref", "refs/heads/main", Commit)]
    // A holder commit only by full ids, with at least one parent, its message from standard input.
    [InlineData("commit-tree", EmptyTree, "-F", "-")]
    [InlineData("commit-tree", "HEAD^{tree}", "-p", Commit, "-F", "-")]
    [InlineData("commit-tree", EmptyTree, "-p", "HEAD", "-F", "-")]
    [InlineData("commit-tree", EmptyTree, "-p", Commit, "-m", "x")]
    [InlineData("commit-tree", EmptyTree, "-p", Commit, "-S", "-F", "-")]
    [InlineData("commit-tree", EmptyTree, "-p", Commit, Other, "-F", "-")]
    [InlineData("commit", "-m", "x")]
    // Never part of a cleanup.
    [InlineData("push", "origin", "--delete", "x")]
    [InlineData("fetch", "--prune", "origin")]
    [InlineData("reset", "--hard", Commit)]
    [InlineData("clean", "-fd")]
    [InlineData("gc", "--prune=now")]
    [InlineData("reflog", "expire", "--all")]
    [InlineData("checkout", "main")]
    [InlineData("switch", "main")]
    [InlineData("-c", "core.hooksPath=x", "branch", "-D", "x")]
    // Reads go through the read-only runner.
    [InlineData("status", "--porcelain=v2")]
    public void Rejects_anything_else(params string[] args)
    {
        Assert.False(Allowed(Line(args)));
    }

    [Fact]
    public void Mktree_may_only_write_the_empty_tree()
    {
        Assert.True(Allowed(Line("mktree")));
        Assert.False(CleanupPolicy.IsAllowed(new GitCommandLine(["mktree"], [$"100644 blob {Commit}\tfile.txt"]), out var reason));
        Assert.Equal("mktree may only write the empty tree", reason);
        Assert.False(Allowed(Line("mktree", "--missing")));
    }

    [Fact]
    public void Update_ref_takes_a_transaction_of_creates_and_expected_deletes()
    {
        Assert.True(Allowed(UpdateRef("start", $"delete refs/copilot/checkpoints/abc/1 {Commit}", $"delete refs/copilot/checkpoints/abc/2 {Other}", "prepare", "commit")));
        Assert.True(Allowed(UpdateRef($"create refs/remotes/origin/done-1 {Commit}", $"create refs/tags/archive/x {Other}", $"create refs/heads/x {Commit}")));
    }

    [Theory]
    // A branch, tag, remote-tracking branch or the stash is deleted by its own command, after a backup.
    [InlineData($"delete refs/heads/main {Commit}")]
    [InlineData($"delete refs/tags/v1.0 {Commit}")]
    [InlineData($"delete refs/remotes/origin/main {Commit}")]
    [InlineData($"delete refs/stash {Commit}")]
    // Without an expected value, or with "must not exist", or moving a ref.
    [InlineData("delete refs/copilot/checkpoints/abc/1")]
    [InlineData("delete refs/copilot/checkpoints/abc/1 0000000000000000000000000000000000000000")]
    [InlineData($"update refs/copilot/checkpoints/abc/1 {Commit} {Other}")]
    [InlineData($"update refs/heads/main {Commit}")]
    [InlineData($"create HEAD {Commit}")]
    [InlineData($"create refs/heads/x HEAD")]
    [InlineData("option no-deref")]
    [InlineData("")]
    public void Update_ref_refuses_any_other_line(string line)
    {
        Assert.False(CleanupPolicy.IsAllowed(UpdateRef($"create refs/heads/ok {Commit}", line), out var reason) && line.Length > 0, reason);
        Assert.False(CleanupPolicy.IsAllowed(UpdateRef(line), out _));
    }

    [Fact]
    public async Task A_cleanup_runner_rejects_anything_else_before_a_process_starts()
    {
        using var temp = new TempDirectory();
        // If the runner tried to start anything, this missing executable would throw Win32Exception instead.
        var runner = new GitRunner(temp.Path, new GitRunnerOptions { GitExecutable = temp.Combine("no-such-git.exe"), CleanupOnly = true });
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<CleanupPolicyViolationException>(() => runner.RunAsync(["worktree", "prune"], ct));
        await Assert.ThrowsAsync<CleanupPolicyViolationException>(() => runner.RunAsync(new GitCommandLine(["update-ref", "--stdin"], [$"delete refs/heads/main {Commit}"]), ct));
        // A read too: the cleanup runner is for cleanups only.
        await Assert.ThrowsAsync<CleanupPolicyViolationException>(() => runner.RunAsync(["status", "--porcelain=v2"], ct));

        Assert.Equal(["worktree", "prune"], refused.Arguments);
        // Control: an allowed command does try to start the missing executable.
        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAsync(["branch", "-D", "x"], ct));
    }
}
