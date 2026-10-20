using System.ComponentModel;
using Diffract.Fixtures;
using Diffract.Git.Parsing;

namespace Diffract.Git.Tests;

public class GitRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Read_only_is_the_default()
    {
        Assert.True(new GitRunnerOptions().ReadOnly);
    }

    [Fact]
    public async Task Read_only_mode_rejects_a_write_without_starting_a_process()
    {
        using var temp = new TempDirectory();
        // If the runner tried to start anything, this missing executable would throw Win32Exception instead.
        var runner = new GitRunner(temp.Path, new GitRunnerOptions { GitExecutable = temp.Combine("no-such-git.exe") });

        var ex = await Assert.ThrowsAsync<ReadOnlyViolationException>(() => runner.RunAsync(["branch", "-D", "main"], Ct));

        Assert.Equal(["branch", "-D", "main"], ex.Arguments);
        // Control: an allowed command does try to start the missing executable.
        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAsync(["version"], Ct));
    }

    [Fact]
    public async Task Returns_output_exit_code_and_duration()
    {
        using var temp = new TempDirectory();
        var runner = IsolatedGit.ReadOnlyRunner(temp, temp.Path);

        var result = await runner.RunAsync(["version"], Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("git version ", result.StandardOutput);
        Assert.Equal(result.StandardOutput, System.Text.Encoding.UTF8.GetString(result.StandardOutputBytes));
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Non_zero_exit_throws_with_command_exit_code_and_stderr()
    {
        using var temp = new TempDirectory();
        await IsolatedGit.WritableRunner(temp, temp.Path).RunAsync(["init", "-q", "-b", "main"], Ct);
        var runner = IsolatedGit.ReadOnlyRunner(temp, temp.Path);

        var ex = await Assert.ThrowsAsync<GitCommandException>(() => runner.RunAsync(["rev-parse", "--verify", "refs/heads/nope"], Ct));

        Assert.Equal(128, ex.ExitCode);
        Assert.Equal(["rev-parse", "--verify", "refs/heads/nope"], ex.Arguments);
        Assert.Contains("fatal", ex.StandardError);
        Assert.Contains("rev-parse --verify refs/heads/nope", ex.Message);
    }

    [Fact]
    public async Task Allowed_exit_codes_are_returned_instead_of_thrown()
    {
        using var temp = new TempDirectory();
        await IsolatedGit.WritableRunner(temp, temp.Path).RunAsync(["init", "-q", "-b", "main"], Ct);
        var runner = IsolatedGit.ReadOnlyRunner(temp, temp.Path);

        var result = await runner.RunAsync(["rev-parse", "--verify", "-q", "refs/heads/nope"], [1], Ct);

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Sets_lock_and_prompt_variables_and_extra_variables_on_every_process()
    {
        using var temp = new TempDirectory();
        var runner = new GitRunner(temp.Path, new GitRunnerOptions
        {
            ReadOnly = false,
            GitExecutable = "cmd.exe",
            // Options must not be able to turn the safety variables off.
            Environment = new Dictionary<string, string> { ["DIFFRACT_TEST"] = "yes", ["GIT_OPTIONAL_LOCKS"] = "1" },
        });

        var result = await runner.RunAsync(["/c", "set"], Ct);

        var lines = result.StandardOutput.Split("\r\n");
        Assert.Contains("GIT_OPTIONAL_LOCKS=0", lines);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", lines);
        Assert.Contains("DIFFRACT_TEST=yes", lines);
    }

    [Fact]
    public async Task Long_paths_are_on_even_when_config_says_otherwise()
    {
        using var temp = new TempDirectory();
        await IsolatedGit.WritableRunner(temp, temp.Path).RunAsync(["init", "-q", "-b", "main"], Ct);
        await IsolatedGit.WritableRunner(temp, temp.Path).RunAsync(["config", "core.longpaths", "false"], Ct);

        var result = await IsolatedGit.ReadOnlyRunner(temp, temp.Path).RunAsync(["config", "--get", "core.longpaths"], Ct);

        Assert.Equal("true", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task Status_never_starts_an_fsmonitor_daemon()
    {
        using var temp = new TempDirectory();
        var writable = IsolatedGit.WritableRunner(temp, temp.Path);
        await writable.RunAsync(["init", "-q", "-b", "main"], Ct);
        await writable.RunAsync(["config", "core.fsmonitor", "true"], Ct);
        File.WriteAllText(temp.Combine("a.txt"), "changed");

        var status = await IsolatedGit.ReadOnlyRunner(temp, temp.Path).RunAsync(["status", "--porcelain=v2", "-z"], Ct);
        var setting = await IsolatedGit.ReadOnlyRunner(temp, temp.Path).RunAsync(["config", "--get", "core.fsmonitor"], Ct);

        Assert.Contains("a.txt", status.StandardOutput);
        Assert.Equal("false", setting.StandardOutput.Trim());
        Assert.False(Directory.Exists(temp.Combine(".git", "fsmonitor--daemon")));
    }

    [Fact]
    public async Task Times_out_and_kills_the_process()
    {
        using var temp = new TempDirectory();
        var runner = new GitRunner(temp.Path, new GitRunnerOptions
        {
            ReadOnly = false,
            GitExecutable = "cmd.exe",
            Timeout = TimeSpan.FromMilliseconds(300),
        });

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<GitTimeoutException>(() => runner.RunAsync(["/c", "ping", "-n", "30", "127.0.0.1"], Ct));

        Assert.Equal(TimeSpan.FromMilliseconds(300), ex.Timeout);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        using var temp = new TempDirectory();
        var runner = new GitRunner(temp.Path, new GitRunnerOptions { ReadOnly = false, GitExecutable = "cmd.exe" });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(["/c", "ping", "-n", "30", "127.0.0.1"], cts.Token));
    }

    [Fact]
    public async Task Round_trips_non_ascii_paths_from_real_git()
    {
        using var temp = new TempDirectory();
        var repo = temp.Combine("repo");
        Directory.CreateDirectory(repo);
        var writer = IsolatedGit.WritableRunner(temp, repo);
        await writer.RunAsync(["init", "-q", "-b", "main"], Ct);
        await writer.RunAsync(["commit", "-q", "--allow-empty", "-m", "Initial"], Ct);
        await writer.RunAsync(["worktree", "add", "-q", "-b", "feature/åäö", temp.Combine("wt med åäö")], Ct);

        var result = await IsolatedGit.ReadOnlyRunner(temp, repo).RunAsync(WorktreeListParser.Arguments, Ct);
        var worktrees = WorktreeListParser.Parse(result.StandardOutput);

        Assert.Equal(2, worktrees.Count);
        Assert.EndsWith("/wt med åäö", worktrees[1].Path);
        Assert.Equal("refs/heads/feature/åäö", worktrees[1].Branch);
    }
}
