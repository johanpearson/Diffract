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
    public async Task Streams_output_and_stops_the_process_when_the_reader_has_enough()
    {
        using var temp = new TempDirectory();
        // ping writes a line a second for 30 seconds, so returning early shows the process was stopped.
        var runner = new GitRunner(temp.Path, new GitRunnerOptions { ReadOnly = false, GitExecutable = "cmd.exe" });
        var started = DateTime.UtcNow;
        var firstBytes = 0;

        var result = await runner.StreamAsync(["/c", "ping", "-n", "30", "127.0.0.1"], async (output, ct) =>
        {
            firstBytes = await output.ReadAsync(new byte[16], ct);
            return false;
        }, [], Ct);

        Assert.True(firstBytes > 0);
        Assert.True(result.StoppedEarly);
        Assert.Null(result.ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Input_lines_reach_git_on_standard_input()
    {
        using var temp = new TempDirectory();
        await IsolatedGit.WritableRunner(temp, temp.Path).RunAsync(["init", "-q", "-b", "main"], Ct);
        var runner = IsolatedGit.ReadOnlyRunner(temp, temp.Path);
        var (a, b) = (new string('a', 40), new string('b', 40));
        var output = new MemoryStream();

        await runner.StreamAsync(new GitCommandLine(["cat-file", "--batch-check"], [a, b]), async (stdout, ct) =>
        {
            await stdout.CopyToAsync(output, ct);
            return true;
        }, Ct);

        Assert.Equal($"{a} missing\n{b} missing\n", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Input_git_never_reads_is_no_error()
    {
        using var temp = new TempDirectory();
        var runner = IsolatedGit.ReadOnlyRunner(temp, temp.Path);
        // Far more than a pipe holds, so writing it blocks until git ends without reading it.
        var lines = Enumerable.Repeat(new string('a', 40), 50_000).ToList();

        var result = await runner.StreamAsync(new GitCommandLine(["version"], lines), async (stdout, ct) =>
        {
            await stdout.CopyToAsync(Stream.Null, ct);
            return true;
        }, Ct);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_reader_that_fails_stops_the_process_and_its_error_is_passed_on()
    {
        using var temp = new TempDirectory();
        var runner = new GitRunner(temp.Path, new GitRunnerOptions { ReadOnly = false, GitExecutable = "cmd.exe" });
        var started = DateTime.UtcNow;

        await Assert.ThrowsAsync<FormatException>(() => runner.StreamAsync(["/c", "ping", "-n", "30", "127.0.0.1"], async (output, ct) =>
        {
            _ = await output.ReadAsync(new byte[16], ct);
            throw new FormatException("Unexpected output");
        }, [], Ct));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_streamed_call_still_checks_the_exit_code()
    {
        using var temp = new TempDirectory();
        await IsolatedGit.WritableRunner(temp, temp.Path).RunAsync(["init", "-q", "-b", "main"], Ct);
        var runner = IsolatedGit.ReadOnlyRunner(temp, temp.Path);

        var ex = await Assert.ThrowsAsync<GitCommandException>(() => runner.StreamAsync(["log", "refs/heads/nope", "--"], async (output, ct) =>
        {
            await output.CopyToAsync(Stream.Null, ct);
            return true;
        }, [], Ct));

        Assert.Equal(128, ex.ExitCode);
    }

    [Fact]
    public async Task Every_started_process_is_reported_whether_it_succeeds_or_fails()
    {
        using var temp = new TempDirectory();
        await IsolatedGit.WritableRunner(temp, temp.Path).RunAsync(["init", "-q", "-b", "main"], Ct);
        List<GitCall> calls = [];
        var runner = new GitRunner(temp.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(temp), OnCall = calls.Add });

        await runner.RunAsync(["version"], Ct);
        await Assert.ThrowsAsync<GitCommandException>(() => runner.RunAsync(["rev-parse", "--verify", "refs/heads/nope"], Ct));
        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => runner.RunAsync(["branch", "x"], Ct));

        Assert.Equal(["version", "rev-parse"], calls.Select(c => c.Arguments[0]));
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
