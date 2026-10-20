using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Spike.Tests;

/// <summary>Runs <c>scan</c> end to end against fixture repos, with isolated git config and a fixed clock.</summary>
public sealed class ScanCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed record Run(int ExitCode, string Output, string Error)
    {
        public string[] Lines => Output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
    }

    private async Task<string> BuildAsync(string scenario) => (await new FixtureRepoBuilder(_temp).BuildAsync(scenario)).RepoPath;

    private Task<Run> ScanAsync(string path, params string[] options) => ScanAsync(path, new GitRunnerOptions(), null, options);

    private async Task<Run> ScanAsync(string path, GitRunnerOptions runnerOptions, string? runningFrom, params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var command = new ScanCommand(
            output, error,
            runnerOptions with { Environment = IsolatedGit.Environment(_temp) },
            new FakeTimeProvider(FixtureRepoBuilder.DefaultNow));
        var exit = await command.RunAsync(path, options, runningFrom, TestContext.Current.CancellationToken);
        return new Run(exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Prints_header_table_and_summary()
    {
        var repo = await BuildAsync("StaleAgentWorktree");

        var run = await ScanAsync(repo);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("", run.Error);
        Assert.Equal($"Diffract spike  {repo}", run.Lines[0]);
        Assert.Matches(@"^main: main   git: \d+\.\d+\.\d+   snapshot: \d+ ms$", run.Lines[1]);
        Assert.Equal(
            [
                "",
                "Verdict       Kind      Name                  Agent        At risk  Reason",
                "Needs review  Worktree  claude/refactor-cart  Claude Code  2        No activity for 9 days, 2 commits exist nowhere else",
                "",
                "1 leftover: 1 needs review",
            ],
            run.Lines[2..]);
    }

    [Fact]
    public async Task Clean_repo_has_no_leftovers()
    {
        var run = await ScanAsync(await BuildAsync("Clean"));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("No leftovers.", run.Lines[^1]);
    }

    [Fact]
    public async Task Paths_with_non_ascii_characters_are_printed_as_is()
    {
        var run = await ScanAsync(await BuildAsync("AwkwardPaths"));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains(run.Lines, l => l.StartsWith("Safe     Worktree  feature/smörgås  -      0        ", StringComparison.Ordinal));
        Assert.Equal("3 leftovers: 3 safe", run.Lines[^1]);
    }

    [Fact]
    public async Task Json_has_snapshot_summary_and_leftovers()
    {
        var repo = await BuildAsync("StaleAgentWorktree");

        var run = await ScanAsync(repo, "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        using var json = JsonDocument.Parse(run.Output);
        var root = json.RootElement;
        Assert.Equal(repo, root.GetProperty("repoRoot").GetString());
        Assert.Equal("main", root.GetProperty("mainBranch").GetString());
        Assert.Equal(2, root.GetProperty("snapshot").GetProperty("worktrees").GetArrayLength());
        Assert.Equal(2, root.GetProperty("snapshot").GetProperty("localBranches").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("needsReview").GetInt32());
        Assert.False(root.TryGetProperty("timings", out _));

        var leftover = Assert.Single(root.GetProperty("leftovers").EnumerateArray().ToList());
        Assert.Equal("Worktree", leftover.GetProperty("kind").GetString());
        Assert.Equal("claude/refactor-cart", leftover.GetProperty("name").GetString());
        Assert.Equal("Claude Code", leftover.GetProperty("agent").GetString());
        Assert.Equal("NeedsReview", leftover.GetProperty("verdict").GetString());
        Assert.Equal(2, leftover.GetProperty("commitsAtRisk").GetInt32());
        Assert.EndsWith(@"\.claude\worktrees\refactor-cart", leftover.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Json_keeps_quotes_readable()
    {
        var run = await ScanAsync(await BuildAsync("ToolStash"), "--json");

        Assert.Contains("\\\"autostash\\\"", run.Output);
        Assert.DoesNotContain("\\u0022", run.Output);
    }

    [Fact]
    public async Task Timing_lists_every_git_call_and_fsck_separately()
    {
        var run = await ScanAsync(await BuildAsync("Clean"), "--timing");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("Git calls, slowest first:", run.Lines);
        Assert.Contains(run.Lines, l => l.EndsWith("git worktree list --porcelain -z", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.StartsWith("fsck: ", StringComparison.Ordinal) && l.EndsWith(" ms", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.StartsWith("Other calls: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Json_with_timing_includes_the_calls()
    {
        var run = await ScanAsync(await BuildAsync("Clean"), "--json", "--timing");

        using var json = JsonDocument.Parse(run.Output);
        var commands = json.RootElement.GetProperty("timings").EnumerateArray().Select(t => t.GetProperty("command").GetString()).ToList();
        Assert.Contains(commands, c => c!.StartsWith("git fsck", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Config_file_overrides_the_defaults()
    {
        var repo = await BuildAsync("StaleAgentWorktree");
        var config = _temp.Combine("diffract.json");
        File.WriteAllText(config, """{ "staleAfterDays": 30 }""");

        var run = await ScanAsync(repo, "--config", config);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("No leftovers.", run.Lines[^1]);
    }

    [Fact]
    public async Task Worktree_the_spike_runs_from_is_not_reported()
    {
        var repo = await BuildAsync("StaleAgentWorktree");
        var worktree = Path.Combine(repo, ".claude", "worktrees", "refactor-cart");

        var run = await ScanAsync(repo, new GitRunnerOptions(), runningFrom: worktree);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("No leftovers.", run.Lines[^1]);
    }

    [Theory]
    [InlineData("ActiveAgentWorktree")]
    [InlineData("UserDirtyWorktree")]
    [InlineData("DetachedWorktree")]
    [InlineData("ToolStash")]
    [InlineData("OrphanedCommit")]
    [InlineData("LeftoverWorktreeFolder")]
    [InlineData("SquashMergedBranch")]
    public async Task Scan_changes_nothing_on_disk(string scenario)
    {
        var repo = await BuildAsync(scenario);
        var scenarioFolder = Path.GetDirectoryName(repo)!;
        var before = DiskState(scenarioFolder);

        var run = await ScanAsync(repo, "--timing");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(before, DiskState(scenarioFolder));
    }

    [Fact]
    public async Task Folder_that_is_not_a_repo_exits_with_2()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("not-a-repo")).FullName;

        var run = await ScanAsync(folder);

        Assert.Equal(ExitCodes.NotAGitRepository, run.ExitCode);
        Assert.Contains("Not a git repository", run.Error);
        Assert.Equal("", run.Output);
    }

    [Fact]
    public async Task Missing_folder_exits_with_2()
    {
        var run = await ScanAsync(_temp.Combine("does-not-exist"));

        Assert.Equal(ExitCodes.NotAGitRepository, run.ExitCode);
    }

    [Fact]
    public async Task Old_git_exits_with_3()
    {
        var repo = await BuildAsync("Clean");
        var fakeGit = _temp.Combine("old-git.cmd");
        File.WriteAllText(fakeGit, "@echo git version 2.39.5.windows.1\r\n");

        var run = await ScanAsync(repo, new GitRunnerOptions { GitExecutable = fakeGit }, null);

        Assert.Equal(ExitCodes.GitTooOld, run.ExitCode);
        Assert.Contains("2.40.0", run.Error);
        Assert.Contains("2.39.5", run.Error);
    }

    [Fact]
    public async Task Missing_git_exits_with_1()
    {
        var repo = await BuildAsync("Clean");

        var run = await ScanAsync(repo, new GitRunnerOptions { GitExecutable = _temp.Combine("no-such-git.exe") }, null);

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("Could not start git", run.Error);
    }

    [Fact]
    public async Task Missing_config_file_exits_with_1()
    {
        var run = await ScanAsync(await BuildAsync("Clean"), "--config", _temp.Combine("missing.json"));

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("Cannot read config file", run.Error);
    }

    [Fact]
    public async Task Invalid_config_exits_with_1()
    {
        var config = _temp.Combine("bad.json");
        File.WriteAllText(config, """{ "staleAfterDays": 0 }""");

        var run = await ScanAsync(await BuildAsync("Clean"), "--config", config);

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("staleAfterDays", run.Error);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--config")]
    [InlineData("--json", "--json")]
    public async Task Unknown_or_incomplete_options_print_usage(params string[] options)
    {
        var run = await ScanAsync(_temp.Path, options);

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.StartsWith("usage: diffract-spike scan", run.Error);
    }

    /// <summary>Every file and folder with its size, write time and content, so any write shows up.</summary>
    private static List<string> DiskState(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):O}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}"
                : $"{path}|dir|{Directory.GetLastWriteTimeUtc(path):O}")
            .ToList();
}
