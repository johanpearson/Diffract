using System.Collections.Concurrent;
using Diffract.Fixtures;
using Diffract.Git.Changes;
using Diffract.Git.Parsing;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Changes;

/// <summary>Commits in real fixture repos, with author, committer and dates set by the fixture's isolated git.</summary>
public sealed class CommitWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunnerOptions Options => new() { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue };

    private CommitWriter WriterFor(FixtureRepo repo, GitRunnerOptions? options = null) => new(repo.Path, options ?? Options);

    /// <summary>A repo with one commit and a staged change to it.</summary>
    private async Task<FixtureRepo> StagedAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("a.txt", "a\n", "Initial commit");
        repo.WriteFile("a.txt", "A\n");
        await repo.GitAsync("add", "a.txt");
        _calls.Clear();
        return repo;
    }

    /// <summary>The last commit in the format. <c>git log</c> ends it with one more line feed, which is left off here.</summary>
    private static async Task<string> LogAsync(FixtureRepo repo, string format) =>
        (await repo.GitAsync("log", "-1", "--format=" + format)).StandardOutput[..^1];

    private static async Task<int> CountAsync(FixtureRepo repo) =>
        int.Parse((await repo.GitAsync("rev-list", "--count", "HEAD")).StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Commits, expecting git or Diffract to refuse.</summary>
    private static Task<CommitRefusedException> RefusedAsync(CommitWriter writer, string message, bool amend = false) =>
        Assert.ThrowsAsync<CommitRefusedException>(() => writer.CommitAsync(new CommitRequest(message, amend), Ct));

    private bool GitCommitRan => _calls.Any(c => c.Arguments[0] == "commit");

    private async Task WriteHookAsync(FixtureRepo repo, string script)
    {
        var hook = Path.Combine(repo.Path, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, script.ReplaceLineEndings("\n"), Ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Commits_what_is_staged_with_the_message_exactly_as_written_in_three_git_calls()
    {
        var repo = await StagedAsync();
        const string message = "Fix the räksmörgås cart\n\nThe total was wrong for åäö items.\n# This line stays, it is not a comment here.\n";

        await WriterFor(repo).CommitAsync(new CommitRequest(message, Amend: false), Ct);

        Assert.Equal(message, await LogAsync(repo, "%B"));
        // The moment, not how git writes its offset, which differs between platforms.
        Assert.Equal($"Fixture Author|fixture@example.com|{IsolatedGit.DefaultDate.ToUnixTimeSeconds()}", await LogAsync(repo, "%an|%ae|%at"));
        Assert.Equal(2, await CountAsync(repo));
        var commit = _calls.Single(c => c.Arguments[0] == "commit");
        Assert.Equal(["commit", "diff", "var"], _calls.Select(c => c.Arguments[0]).Order());
        Assert.True(ChangesPolicy.IsAllowed(commit.Arguments, out _));
    }

    [Fact]
    public async Task The_first_commit_of_a_new_repo()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "new");
        repo.WriteFile("first.txt", "first\n");
        await repo.GitAsync("add", "first.txt");

        await WriterFor(repo).CommitAsync(new CommitRequest("First", Amend: false), Ct);

        Assert.Equal(1, await CountAsync(repo));
    }

    [Fact]
    public async Task Nothing_staged_is_refused_before_git_commits()
    {
        var repo = await StagedAsync();
        await repo.GitAsync("restore", "--staged", "a.txt");
        _calls.Clear();

        var e = await RefusedAsync(WriterFor(repo), "Nothing");

        Assert.Equal("Nothing is staged, so there is nothing to commit.", e.Message);
        Assert.Equal((false, 1), (GitCommitRan, await CountAsync(repo)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\nBody without a subject")]
    public async Task A_message_without_a_subject_is_refused_before_git_runs(string message)
    {
        var repo = await StagedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo).CommitAsync(new CommitRequest(message, false), Ct));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_missing_name_or_email_is_refused_in_plain_words_before_git_commits()
    {
        var repo = await StagedAsync();
        await repo.GitAsync("config", "user.useConfigOnly", "true");
        var environment = IsolatedGit.Environment(_temp).Where(v => !v.Key.StartsWith("GIT_AUTHOR_", StringComparison.Ordinal) && !v.Key.StartsWith("GIT_COMMITTER_", StringComparison.Ordinal));
        var options = Options with { Environment = environment.ToDictionary() };

        var e = await RefusedAsync(WriterFor(repo, options), "No identity");

        Assert.StartsWith("Git doesn't know your name and email", e.Message);
        Assert.Contains("auto-detection is disabled", e.GitOutput);
        Assert.False(GitCommitRan);
    }

    [Fact]
    public async Task A_failing_pre_commit_hook_shows_its_output_and_commits_nothing()
    {
        var repo = await StagedAsync();
        await WriteHookAsync(repo, "#!/bin/sh\necho \"Checking a.txt\"\necho \"lint failed: a.txt line 1\" >&2\nexit 1\n");

        var e = await RefusedAsync(WriterFor(repo), "Blocked");

        Assert.StartsWith("Git did not commit.", e.Message);
        Assert.Contains("Checking a.txt", e.GitOutput);
        Assert.Contains("lint failed: a.txt line 1", e.GitOutput);
        var file = (await new ChangesReader(Options).ReadStatusAsync(repo.Path, Ct)).Files.Single();
        Assert.Equal((1, FileChangeKind.Modified, (FileChangeKind?)null), (await CountAsync(repo), file.Staged, file.Unstaged));
    }

    [Fact]
    public async Task A_passing_hook_runs_and_the_commit_goes_through()
    {
        var repo = await StagedAsync();
        await WriteHookAsync(repo, "#!/bin/sh\necho ran > hook-ran.txt\nexit 0\n");

        await WriterFor(repo).CommitAsync(new CommitRequest("Hooked", false), Ct);

        Assert.True(File.Exists(Path.Combine(repo.Path, "hook-ran.txt")));
        Assert.Equal(2, await CountAsync(repo));
    }

    [Fact]
    public async Task A_failing_signature_shows_gits_message_and_commits_nothing()
    {
        var repo = await StagedAsync();
        await repo.GitAsync("config", "commit.gpgsign", "true");
        await repo.GitAsync("config", "gpg.program", Path.Combine(_temp.Path, "no-such-gpg"));

        var e = await RefusedAsync(WriterFor(repo), "Signed");

        Assert.Contains("gpg failed to sign", e.GitOutput);
        Assert.Equal(1, await CountAsync(repo));
    }

    [Fact]
    public async Task Amend_replaces_the_last_commit_when_it_is_not_pushed()
    {
        var repo = await StagedAsync();
        await repo.GitAsync("commit", "-q", "-m", "Typo in the mesage");
        var parent = await repo.RevParseAsync("HEAD~1");

        await WriterFor(repo).CommitAsync(new CommitRequest("Fix the message", Amend: true), Ct);

        Assert.Equal("Fix the message\n", await LogAsync(repo, "%B"));
        Assert.Equal(parent, await repo.RevParseAsync("HEAD~1"));
        Assert.Equal(2, await CountAsync(repo));
    }

    [Fact]
    public async Task Amend_is_refused_when_the_last_commit_is_on_a_remote_branch()
    {
        var repo = await StagedAsync();
        var origin = await FixtureRepo.InitAsync(_temp, "origin.git", bare: true);
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "origin", "main");
        var head = await repo.RevParseAsync("HEAD");

        var e = await RefusedAsync(WriterFor(repo), "Rewrite", amend: true);

        Assert.Equal("The last commit is already on origin/main, so amending it would rewrite pushed work.", e.Message);
        Assert.Equal(head, await repo.RevParseAsync("HEAD"));
    }

    [Fact]
    public async Task Amend_before_the_first_commit_is_refused()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "new");
        repo.WriteFile("first.txt", "first\n");
        await repo.GitAsync("add", "first.txt");

        var e = await RefusedAsync(WriterFor(repo), "First", amend: true);

        Assert.Equal("There is no commit to amend yet.", e.Message);
    }

    [Fact]
    public async Task A_locked_index_is_waited_on_then_reported_and_the_lock_stays()
    {
        var repo = await StagedAsync();
        var lockFile = GitDirectory.FindIndexLock(repo.Path)!;
        await File.WriteAllTextAsync(lockFile, "", Ct);

        await Assert.ThrowsAsync<IndexLockedException>(() => WriterFor(repo).CommitAsync(new CommitRequest("Locked", false), Ct));

        Assert.True(File.Exists(lockFile));
        File.Delete(lockFile);
        Assert.Equal(1, await CountAsync(repo));
    }

    [Fact]
    public async Task Status_names_the_remote_branch_that_has_head_and_nothing_before_it_is_pushed()
    {
        var repo = await StagedAsync();
        var reader = new ChangesReader(Options);
        Assert.Null((await reader.ReadStatusAsync(repo.Path, Ct)).HeadOnRemote);

        var origin = await FixtureRepo.InitAsync(_temp, "origin.git", bare: true);
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "origin", "main");
        await repo.GitAsync("remote", "set-head", "origin", "main");

        Assert.Equal("origin/main", (await reader.ReadStatusAsync(repo.Path, Ct)).HeadOnRemote);
        Assert.Null((await reader.ReadStatusAsync((await FixtureRepo.InitAsync(_temp, "new")).Path, Ct)).HeadOnRemote);
    }
}
