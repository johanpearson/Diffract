using System.Collections.Concurrent;
using System.Diagnostics;
using Diffract.Fixtures;
using Diffract.Git.Files;
using Diffract.Git.History;
using Diffract.Git.Parsing;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.History;

/// <summary>
/// The history reader against real fixture repos. Starting git is the expensive part on Windows, so every test
/// also checks how many git processes it took.
/// </summary>
public sealed class HistoryReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunnerOptions Options => new() { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue };

    private HistoryReader Reader => new(Options);

    private HistoryReader ReaderWithPages(int pageSize) => new(Options, pageSize);

    private async Task<(int Total, List<IReadOnlyList<CommitEntry>> Pages)> ReadAllAsync(string repo, IReadOnlyCollection<string> tips, int pageSize = HistoryReader.PageSize)
    {
        List<IReadOnlyList<CommitEntry>> pages = [];
        var total = await ReaderWithPages(pageSize).ReadAsync(repo, tips, page =>
        {
            pages.Add(page);
            return Task.CompletedTask;
        }, Ct);
        return (total, pages);
    }

    [Fact]
    public void Refuses_a_runner_that_may_write()
    {
        Assert.Throws<ArgumentException>(() => new HistoryReader(new GitRunnerOptions { ReadOnly = false }));
    }

    [Fact]
    public async Task Reads_the_whole_history_in_pages_with_one_git_call()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        for (var i = 1; i <= 12; i++)
            await repo.CommitAsync("file.txt", $"{i}", $"Commit {i}");
        _calls.Clear();

        var (total, pages) = await ReadAllAsync(repo.Path, await TipsAsync(repo.Path), pageSize: 5);

        Assert.Equal(12, total);
        Assert.Equal([5, 5, 2], pages.Select(p => p.Count));
        Assert.Equal("Commit 12", pages[0][0].Subject);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task Reads_50000_commits_with_one_git_call_and_the_first_page_comes_quickly()
    {
        var large = await LargeHistory.BuildAsync(_temp, "large", commits: 50_000);
        var tips = await TipsAsync(large.Path);
        _calls.Clear();
        var clock = Stopwatch.StartNew();
        TimeSpan? firstPage = null;
        var pages = 0;

        var total = await Reader.ReadAsync(large.Path, tips, page =>
        {
            firstPage ??= clock.Elapsed;
            pages++;
            return Task.CompletedTask;
        }, Ct);

        var first = Assert.NotNull(firstPage);
        TestContext.Current.TestOutputHelper?.WriteLine($"50,000 commits: first page after {first.TotalMilliseconds:0} ms, all after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(50_000, total);
        Assert.Equal(100, pages);
        Assert.Single(_calls);
        // Phase 0 budget: the first rows on screen within a second. This leaves the UI its share,
        // plus slack for a busy CI runner.
        Assert.InRange(first, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Shows_branches_remote_branches_tags_and_worktree_heads_but_not_checkpoints_or_the_stash()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("file.txt", "1", "On main");
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("file.txt", "2", "On a branch");
        await repo.GitAsync("update-ref", "refs/remotes/origin/feature", "HEAD");
        await repo.CommitAsync("file.txt", "3", "Only on a remote branch");
        await repo.GitAsync("update-ref", "refs/remotes/origin/only", "HEAD");
        await repo.GitAsync("reset", "-q", "--hard", "HEAD~1");
        await repo.CommitAsync("file.txt", "4", "Only on a tag");
        await repo.GitAsync("tag", "-a", "v1", "-m", "Tagged");
        await repo.GitAsync("reset", "-q", "--hard", "HEAD~1");
        await repo.CommitAsync("file.txt", "5", "Only on a checkpoint");
        await repo.GitAsync("update-ref", "refs/copilot/checkpoints/session/1", "HEAD");
        await repo.GitAsync("reset", "-q", "--hard", "HEAD~1");
        repo.WriteFile("file.txt", "stashed");
        await repo.GitAsync("stash", "push", "-q", "-m", "Only in the stash");
        await repo.GitAsync("worktree", "add", "-q", "--detach", _temp.Combine("detached"), "main");
        await repo.At(_temp.Combine("detached")).CommitAsync("other.txt", "6", "Only on a detached worktree");
        var snapshot = await SnapshotAsync(repo.Path);

        var (_, pages) = await ReadAllAsync(repo.Path, HistoryReader.TipsOf(snapshot));

        Assert.Equal(
            new[] { "On main", "On a branch", "Only on a remote branch", "Only on a tag", "Only on a detached worktree" }.Order(),
            pages.SelectMany(p => p).Select(c => c.Subject).Order());
    }

    [Fact]
    public async Task An_empty_repo_has_no_history_no_error_and_needs_no_git()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "empty");
        var tips = await TipsAsync(repo.Path);
        _calls.Clear();

        var (total, pages) = await ReadAllAsync(repo.Path, tips);

        Assert.Equal(0, total);
        Assert.Empty(pages);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_tag_on_a_file_instead_of_a_commit_is_left_out_without_an_error()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("file.txt", "1", "On main");
        var blob = await repo.RevParseAsync("HEAD:file.txt");
        await repo.GitAsync("tag", "file-tag", blob);

        var (total, _) = await ReadAllAsync(repo.Path, await TipsAsync(repo.Path));

        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Hundreds_of_tips_do_not_hit_the_command_line_limit()
    {
        var large = await LargeHistory.BuildAsync(_temp, "large", commits: 3000);
        // Every commit as a tip: 3,000 ids hold over 120,000 characters, far past the 32,767 of a Windows command line.
        var all = await IsolatedGit.ReadOnlyRunner(_temp, large.Path).RunAsync(["rev-list", "--all"], Ct);
        var tips = all.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _calls.Clear();

        var (total, _) = await ReadAllAsync(large.Path, tips);

        Assert.Equal(3000, tips.Length);
        Assert.Equal(3000, total);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task Cancelling_while_pages_arrive_stops_git()
    {
        var large = await LargeHistory.BuildAsync(_temp, "large", commits: 3000);
        var tips = await TipsAsync(large.Path);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _calls.Clear();
        var pages = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReaderWithPages(100).ReadAsync(large.Path, tips, async _ =>
        {
            if (++pages == 2)
                await cancel.CancelAsync();
        }, cancel.Token));

        Assert.Equal(2, pages);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task Details_take_one_git_call()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("a.txt", "a", "First");
        var second = await repo.CommitAsync("b.txt", "b", "Second\n\nWith a body.");
        _calls.Clear();

        var details = await Reader.ReadDetailsAsync(repo.Path, second, Ct);

        Assert.Equal("Second", details.Subject);
        Assert.Equal("With a body.", details.Body);
        Assert.Equal(["b.txt"], details.Files.Select(f => f.Path));
        Assert.Single(_calls);
    }

    [Fact]
    public async Task Details_of_an_unknown_commit_are_a_git_error()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("a.txt", "a", "First");

        await Assert.ThrowsAsync<GitCommandException>(() => Reader.ReadDetailsAsync(repo.Path, new string('1', 40), Ct));
    }

    [Fact]
    public async Task Both_sides_of_a_change_take_one_git_call_each()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("a.txt", "before\n", "First");
        var commit = await repo.CommitAsync("a.txt", "after\n", "Second");
        var file = (await Reader.ReadDetailsAsync(repo.Path, commit, Ct)).Files.Single();
        _calls.Clear();

        var before = await Reader.ReadFileAsync(repo.Path, file.OldId!, Ct);
        var after = await Reader.ReadFileAsync(repo.Path, file.NewId!, Ct);

        Assert.Equal(new FileContent(FileContentKind.Text, "before\n"), before);
        Assert.Equal(new FileContent(FileContentKind.Text, "after\n"), after);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task Text_keeps_its_line_endings_and_non_ascii_and_loses_its_byte_order_mark()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await File.WriteAllBytesAsync(repo.Path + "/bom.txt", [0xEF, 0xBB, 0xBF, .. "räka\r\nsmörgås\n"u8.ToArray()], Ct);
        await repo.GitAsync("add", "bom.txt");
        await repo.GitAsync("commit", "-q", "-m", "BOM");

        var content = await Reader.ReadFileAsync(repo.Path, await repo.RevParseAsync("HEAD:bom.txt"), Ct);

        Assert.Equal(new FileContent(FileContentKind.Text, "räka\r\nsmörgås\n"), content);
    }

    [Fact]
    public async Task A_binary_file_is_not_sent()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await File.WriteAllBytesAsync(repo.Path + "/image.bin", [.. "PNG"u8.ToArray(), 0, .. new byte[200_000]], Ct);
        await repo.GitAsync("add", "image.bin");
        await repo.GitAsync("commit", "-q", "-m", "Binary");

        var content = await Reader.ReadFileAsync(repo.Path, await repo.RevParseAsync("HEAD:image.bin"), Ct);

        Assert.Equal(new FileContent(FileContentKind.Binary, null), content);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_and_git_is_stopped()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        repo.WriteFile("big.txt", string.Concat(Enumerable.Repeat("a line of text\n", 100_000)));
        await repo.GitAsync("add", "big.txt");
        await repo.GitAsync("commit", "-q", "-m", "Big");
        var blob = await repo.RevParseAsync("HEAD:big.txt");

        Assert.Equal(new FileContent(FileContentKind.TooLarge, null), await Reader.ReadFileAsync(repo.Path, blob, Ct, maxBytes: 64 * 1024));
        Assert.Equal(FileContentKind.Text, (await Reader.ReadFileAsync(repo.Path, blob, Ct, maxBytes: 1_500_000)).Kind);
    }

    [Fact]
    public async Task A_file_exactly_at_the_limit_is_sent()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        repo.WriteFile("exact.txt", new string('x', 1000));
        await repo.GitAsync("add", "exact.txt");
        await repo.GitAsync("commit", "-q", "-m", "Exact");

        var content = await Reader.ReadFileAsync(repo.Path, await repo.RevParseAsync("HEAD:exact.txt"), Ct, maxBytes: 1000);

        Assert.Equal(FileContentKind.Text, content.Kind);
    }

    [Theory]
    [InlineData("HEAD:a.txt")]
    [InlineData("--batch-all-objects")]
    public async Task Reading_a_file_refuses_anything_but_an_object_id(string blob)
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        _calls.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ReadFileAsync(repo.Path, blob, Ct));
        Assert.Empty(_calls);
    }

    private async Task<IReadOnlyList<string>> TipsAsync(string repo) => HistoryReader.TipsOf(await SnapshotAsync(repo));

    private async Task<RepoSnapshot> SnapshotAsync(string repo) =>
        (await new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }, new SnapshotOptions()).BuildAsync(repo, Ct)).Snapshot;
}
