using System.Collections.Concurrent;
using System.Diagnostics;
using Diffract.Fixtures;
using Diffract.Git.Changes;
using Diffract.Git.Parsing;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.Changes;

/// <summary>
/// Staging and unstaging against real fixture repos, for every case from phase 0. After each, the test reads what
/// the index now holds for the file, so it proves exactly that hunk was staged and nothing else.
/// </summary>
public sealed class ChangesWriterTests : IDisposable
{
    private static readonly string Lines = Numbered();

    private readonly TempDirectory _temp = new();

    /// <summary>Twenty lines "1" to "20", with the given lines changed.</summary>
    private static string Numbered(params (int Line, string Text)[] changes) =>
        string.Concat(Enumerable.Range(1, 20).Select(i => (changes.FirstOrDefault(c => c.Line == i).Text ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\n"));
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunnerOptions Options => new() { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue };

    private ChangesWriter WriterFor(string worktree) => new(worktree, Options);

    private ChangesReader Reader => new(Options);

    private async Task<FixtureRepo> RepoAsync(params (string Path, string Content)[] files)
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        foreach (var (path, content) in files)
            repo.WriteFile(path, content);
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "--allow-empty", "-m", "Initial commit");
        return repo;
    }

    private static string Crlf(string text) => text.Replace("\n", "\r\n");

    /// <summary>What the index holds for the file, as stored: line endings as in the repo.</summary>
    private static async Task<string> IndexAsync(FixtureRepo repo, string path) =>
        (await repo.GitAsync("cat-file", "blob", ":" + path)).StandardOutput;

    /// <summary>The hunks the UI would show for the file, with their ids.</summary>
    private async Task<IReadOnlyList<Hunk>> HunksAsync(FixtureRepo repo, string path, bool staged)
    {
        var file = (await Reader.ReadStatusAsync(repo.Path, Ct)).Files.Single(f => f.Path == path);
        var request = new FileDiffRequest { Path = path, OldPath = file.OldPath, Staged = staged, HeadId = file.HeadId, IndexId = file.IndexId };
        return (await Reader.ReadFileAsync(repo.Path, request, Ct)).Hunks;
    }

    private async Task StageHunkAsync(FixtureRepo repo, string path, int index)
    {
        var hunks = await HunksAsync(repo, path, staged: false);
        _calls.Clear();
        await WriterFor(repo.Path).StageHunkAsync(new HunkRequest(path, null, hunks[index].Id), Ct);
    }

    private async Task<ChangedFile> StatusOfAsync(FixtureRepo repo, string path) =>
        (await Reader.ReadStatusAsync(repo.Path, Ct)).Files.Single(f => f.Path == path);

    // --- whole files ------------------------------------------------------------

    [Fact]
    public async Task Stages_any_number_of_new_changed_and_deleted_files_with_one_git_call()
    {
        var repo = await RepoAsync(("changed.txt", "a\n"), ("deleted.txt", "d\n"));
        repo.WriteFile("changed.txt", "b\n");
        File.Delete(Path.Combine(repo.Path, "deleted.txt"));
        repo.WriteFile("new folder/räksmörgås med mellanslag.txt", "ny\n");
        _calls.Clear();

        await WriterFor(repo.Path).StageAsync(["changed.txt", "deleted.txt", "new folder/räksmörgås med mellanslag.txt"], Ct);

        Assert.Single(_calls);
        var files = (await Reader.ReadStatusAsync(repo.Path, Ct)).Files;
        Assert.Equal(
            [("changed.txt", FileChangeKind.Modified), ("deleted.txt", FileChangeKind.Deleted), ("new folder/räksmörgås med mellanslag.txt", FileChangeKind.Added)],
            files.Select(f => (f.Path, f.Staged!.Value)));
        Assert.All(files, f => Assert.Null(f.Unstaged));
    }

    /// <summary>
    /// A deleted file named "[ab].txt" no longer matches itself on disk, so as a pattern git would take it to mean
    /// a.txt or b.txt, and stage a.txt's change along with the deletion. Both names are allowed on Windows.
    /// </summary>
    private async Task<FixtureRepo> PatternLikeDeletionAsync()
    {
        var repo = await RepoAsync(("[ab].txt", "brackets\n"), ("a.txt", "a\n"));
        File.Delete(Path.Combine(repo.Path, "[ab].txt"));
        repo.WriteFile("a.txt", "A\n");
        return repo;
    }

    [Fact]
    public async Task A_path_that_looks_like_a_pattern_stages_that_file_and_nothing_else()
    {
        var repo = await PatternLikeDeletionAsync();

        await WriterFor(repo.Path).StageAsync(["[ab].txt"], Ct);

        Assert.Equal(FileChangeKind.Deleted, (await StatusOfAsync(repo, "[ab].txt")).Staged);
        Assert.Equal((null, FileChangeKind.Modified), ((await StatusOfAsync(repo, "a.txt")).Staged, (await StatusOfAsync(repo, "a.txt")).Unstaged));
    }

    [Fact]
    public async Task Without_literal_paths_git_would_have_staged_more()
    {
        var repo = await PatternLikeDeletionAsync();

        await repo.GitAsync("add", "--", "[ab].txt");

        Assert.Equal(FileChangeKind.Modified, (await StatusOfAsync(repo, "a.txt")).Staged);
    }

    [Fact]
    public async Task Unstages_files_and_leaves_them_as_they_are_on_disk()
    {
        var repo = await RepoAsync(("a.txt", "a\n"), ("b.txt", "b\n"));
        repo.WriteFile("a.txt", "A\n");
        repo.WriteFile("b.txt", "B\n");
        await repo.GitAsync("add", "a.txt", "b.txt");

        await WriterFor(repo.Path).UnstageAsync(["a.txt", "b.txt"], Ct);

        Assert.All((await Reader.ReadStatusAsync(repo.Path, Ct)).Files, f => Assert.Equal((null, FileChangeKind.Modified), (f.Staged, f.Unstaged)));
        Assert.Equal("A\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, "a.txt"), Ct));
    }

    [Fact]
    public async Task Unstaging_both_paths_of_a_rename_puts_the_old_file_back_in_the_index()
    {
        var repo = await RepoAsync(("old.txt", "content\n"));
        await repo.GitAsync("mv", "old.txt", "ny räka.txt");

        await WriterFor(repo.Path).UnstageAsync(["ny räka.txt", "old.txt"], Ct);

        var files = (await Reader.ReadStatusAsync(repo.Path, Ct)).Files.ToDictionary(f => f.Path);
        Assert.Equal((null, FileChangeKind.Deleted), (files["old.txt"].Staged, files["old.txt"].Unstaged));
        Assert.Equal(ChangedFileKind.Untracked, files["ny räka.txt"].Kind);
    }

    [Fact]
    public async Task Unstaging_before_the_first_commit_takes_the_file_out_of_the_index_and_keeps_it_on_disk()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "empty");
        repo.WriteFile("first.txt", "first\n");
        await repo.GitAsync("add", "first.txt");
        _calls.Clear();

        await WriterFor(repo.Path).UnstageAsync(["first.txt"], Ct);

        Assert.Equal(["rev-parse", "rm"], _calls.Select(c => c.Arguments[0]));
        Assert.Equal(ChangedFileKind.Untracked, (await StatusOfAsync(repo, "first.txt")).Kind);
        Assert.Equal("first\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, "first.txt"), Ct));
    }

    [Fact]
    public async Task Unstaging_after_the_first_commit_restores_from_head()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        repo.WriteFile("a.txt", "A\n");
        await repo.GitAsync("add", "a.txt");
        _calls.Clear();

        await WriterFor(repo.Path).UnstageAsync(["a.txt"], Ct);

        Assert.Equal(["rev-parse", "restore"], _calls.Select(c => c.Arguments[0]));
    }

    [Fact]
    public async Task A_folder_with_a_repo_of_its_own_is_never_staged()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        await repo.GitAsync("worktree", "add", "-q", "-b", "claude/task", Path.Combine(repo.Path, ".claude", "worktrees", "task"));
        _calls.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo.Path).StageAsync([".claude/worktrees/task"], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo.Path).StageAsync(["a.txt", ".claude"], Ct));

        Assert.Empty(_calls);
        Assert.Equal(ChangedFileKind.NestedRepo, (await StatusOfAsync(repo, ".claude/worktrees/task")).Kind);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".git/config")]
    [InlineData("")]
    public async Task A_path_outside_the_worktree_is_refused_before_git_runs(string path)
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        _calls.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo.Path).StageAsync(["a.txt", path], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo.Path).UnstageAsync([path], Ct));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task No_paths_at_all_are_refused()
    {
        var repo = await RepoAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo.Path).StageAsync([], Ct));
    }

    // --- hunks, every case from phase 0 -------------------------------------------

    [Fact]
    public async Task Of_two_changes_far_apart_only_the_second_is_staged_with_two_git_calls()
    {
        var repo = await RepoAsync(("file.txt", Lines));
        repo.WriteFile("file.txt", Numbered((2, "TWO"), (18, "EIGHTEEN")));

        await StageHunkAsync(repo, "file.txt", 1);

        Assert.Equal(Numbered((18, "EIGHTEEN")), await IndexAsync(repo, "file.txt"));
        Assert.Equal(["diff", "apply"], _calls.Select(c => c.Arguments[0]));
    }

    [Fact]
    public async Task Changes_four_lines_apart_are_two_hunks_and_each_stages_alone()
    {
        var repo = await RepoAsync(("file.txt", Lines));
        repo.WriteFile("file.txt", Numbered((2, "TWO"), (6, "SIX")));

        Assert.Equal(2, (await HunksAsync(repo, "file.txt", staged: false)).Count);
        await StageHunkAsync(repo, "file.txt", 0);

        Assert.Equal(Numbered((2, "TWO")), await IndexAsync(repo, "file.txt"));
    }

    [Fact]
    public async Task With_autocrlf_a_crlf_file_stages_one_hunk_and_the_index_keeps_lf()
    {
        var repo = await RepoAsync();
        await repo.GitAsync("config", "core.autocrlf", "true");
        repo.WriteFile("crlf.txt", Crlf(Lines));
        await repo.GitAsync("add", "crlf.txt");
        await repo.GitAsync("commit", "-q", "-m", "CRLF file");
        repo.WriteFile("crlf.txt", Crlf(Numbered((2, "TWO"), (18, "EIGHTEEN"))));

        await StageHunkAsync(repo, "crlf.txt", 0);

        Assert.Equal(Numbered((2, "TWO")), await IndexAsync(repo, "crlf.txt"));
    }

    [Fact]
    public async Task Crlf_stored_in_the_repo_keeps_its_crs_in_the_index()
    {
        var repo = await RepoAsync(("crlf.txt", Crlf(Lines)));
        repo.WriteFile("crlf.txt", Crlf(Numbered((2, "TWO"), (18, "EIGHTEEN"))));

        await StageHunkAsync(repo, "crlf.txt", 1);

        Assert.Equal(Crlf(Numbered((18, "EIGHTEEN"))), await IndexAsync(repo, "crlf.txt"));
    }

    [Fact]
    public async Task Mixed_line_endings_each_keep_their_own()
    {
        const string mixed = "1\r\n2\n3\r\n4\n5\r\n6\n7\r\n8\n9\r\n10\n";
        var repo = await RepoAsync(("mixed.txt", mixed));
        repo.WriteFile("mixed.txt", mixed.Replace("2\n", "TWO\n").Replace("9\r\n", "NINE\r\n"));

        await StageHunkAsync(repo, "mixed.txt", 1);

        Assert.Equal(mixed.Replace("9\r\n", "NINE\r\n"), await IndexAsync(repo, "mixed.txt"));
    }

    [Fact]
    public async Task A_file_without_a_final_newline_stages_a_hunk_after_its_last_line()
    {
        var repo = await RepoAsync(("file.txt", "first\n2\n3\n4\n5\n6\n7\nlast"));
        repo.WriteFile("file.txt", "FIRST\n2\n3\n4\n5\n6\n7\nlast\nafter");

        await StageHunkAsync(repo, "file.txt", 1);

        Assert.Equal("first\n2\n3\n4\n5\n6\n7\nlast\nafter", await IndexAsync(repo, "file.txt"));
    }

    [Fact]
    public async Task A_new_file_added_with_intent_to_add_stages_as_a_hunk()
    {
        var repo = await RepoAsync();
        repo.WriteFile("new.txt", "new\nfile\n");
        await repo.GitAsync("add", "--intent-to-add", "new.txt");
        var diff = await repo.GitAsync([.. FilePatch.Arguments(new DiffTarget("new.txt", null, Staged: false))]);
        var hunk = Assert.Single(FilePatch.Parse(diff.StandardOutputBytes).Hunks);

        await WriterFor(repo.Path).StageHunkAsync(new HunkRequest("new.txt", null, hunk.Id), Ct);

        Assert.Equal("new\nfile\n", await IndexAsync(repo, "new.txt"));
    }

    [Fact]
    public async Task A_hunk_of_a_renamed_file_stages_and_the_rename_stays()
    {
        var repo = await RepoAsync(("old.txt", Lines));
        await repo.GitAsync("mv", "old.txt", "renamed.txt");
        repo.WriteFile("renamed.txt", Numbered((2, "TWO"), (18, "EIGHTEEN")));

        await StageHunkAsync(repo, "renamed.txt", 0);

        Assert.Equal(Numbered((2, "TWO")), await IndexAsync(repo, "renamed.txt"));
        var file = await StatusOfAsync(repo, "renamed.txt");
        Assert.Equal((FileChangeKind.Renamed, "old.txt", FileChangeKind.Modified), (file.Staged, file.OldPath, file.Unstaged));
    }

    [Fact]
    public async Task Spaces_and_non_ascii_in_the_path_and_the_content()
    {
        const string path = "mapp med å/räksmörgås fil.txt";
        var repo = await RepoAsync((path, Numbered((5, "räka"))));
        repo.WriteFile(path, Numbered((5, "smörgås"), (19, "ö")));

        await StageHunkAsync(repo, path, 0);

        Assert.Equal(Numbered((5, "smörgås")), await IndexAsync(repo, path));
    }

    [Fact]
    public async Task Unstages_one_of_two_staged_hunks()
    {
        var repo = await RepoAsync(("file.txt", Lines));
        repo.WriteFile("file.txt", Numbered((2, "TWO"), (18, "EIGHTEEN")));
        await repo.GitAsync("add", "file.txt");
        var hunks = await HunksAsync(repo, "file.txt", staged: true);

        await WriterFor(repo.Path).UnstageHunkAsync(new HunkRequest("file.txt", null, hunks[0].Id), Ct);

        Assert.Equal(Numbered((18, "EIGHTEEN")), await IndexAsync(repo, "file.txt"));
        Assert.Contains("TWO", await File.ReadAllTextAsync(Path.Combine(repo.Path, "file.txt"), Ct));
    }

    [Fact]
    public async Task Unstages_a_hunk_of_a_staged_rename()
    {
        var repo = await RepoAsync(("old.txt", Lines));
        await repo.GitAsync("mv", "old.txt", "renamed.txt");
        repo.WriteFile("renamed.txt", Numbered((2, "TWO"), (18, "EIGHTEEN")));
        await repo.GitAsync("add", "renamed.txt");
        var hunks = await HunksAsync(repo, "renamed.txt", staged: true);

        await WriterFor(repo.Path).UnstageHunkAsync(new HunkRequest("renamed.txt", "old.txt", hunks[1].Id), Ct);

        Assert.Equal(Numbered((2, "TWO")), await IndexAsync(repo, "renamed.txt"));
        Assert.Equal(FileChangeKind.Renamed, (await StatusOfAsync(repo, "renamed.txt")).Staged);
    }

    [Fact]
    public async Task A_file_that_changed_after_it_was_shown_stages_nothing()
    {
        var repo = await RepoAsync(("file.txt", Lines));
        repo.WriteFile("file.txt", Numbered((2, "TWO")));
        var shown = Assert.Single(await HunksAsync(repo, "file.txt", staged: false));
        repo.WriteFile("file.txt", Numbered((2, "TWO"), (18, "EIGHTEEN")));

        var e = await Assert.ThrowsAsync<ChangedSinceShownException>(() =>
            WriterFor(repo.Path).StageHunkAsync(new HunkRequest("file.txt", null, shown.Id), Ct));

        Assert.Equal("file.txt", e.Path);
        Assert.Equal(Lines, await IndexAsync(repo, "file.txt"));
    }

    // --- a locked index -------------------------------------------------------------

    [Fact]
    public async Task While_another_git_holds_the_index_lock_it_waits_two_seconds_then_gives_up_and_leaves_the_lock()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        repo.WriteFile("a.txt", "A\n");
        var lockFile = GitDirectory.FindIndexLock(repo.Path)!;
        await File.WriteAllTextAsync(lockFile, "", Ct);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<IndexLockedException>(() => WriterFor(repo.Path).StageAsync(["a.txt"], Ct));

        Assert.InRange(clock.Elapsed, IndexLockRetry.Patience, IndexLockRetry.Patience * 5);
        Assert.True(File.Exists(lockFile));
        File.Delete(lockFile);
        Assert.Null((await StatusOfAsync(repo, "a.txt")).Staged);
    }

    [Fact]
    public async Task A_lock_released_while_it_waits_lets_the_change_through()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        repo.WriteFile("a.txt", "A\n");
        var lockFile = GitDirectory.FindIndexLock(repo.Path)!;
        await File.WriteAllTextAsync(lockFile, "", Ct);

        var staging = WriterFor(repo.Path).StageAsync(["a.txt"], Ct);
        await Task.Delay(300, Ct);
        File.Delete(lockFile);
        await staging;

        Assert.Equal(FileChangeKind.Modified, (await StatusOfAsync(repo, "a.txt")).Staged);
    }

    [Fact]
    public async Task The_lock_of_a_linked_worktree_is_its_own()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        var linked = _temp.Combine("linked");
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature", linked);
        await File.WriteAllTextAsync(Path.Combine(linked, "a.txt"), "A\n", Ct);
        await File.WriteAllTextAsync(GitDirectory.FindIndexLock(repo.Path)!, "", Ct);

        await WriterFor(linked).StageAsync(["a.txt"], Ct);

        Assert.EndsWith(Path.Combine("worktrees", "linked", "index.lock"), GitDirectory.FindIndexLock(linked));
        Assert.Equal(FileChangeKind.Modified, (await Reader.ReadStatusAsync(linked, Ct)).Files.Single().Staged);
    }

    // --- the changes runner -----------------------------------------------------------

    [Fact]
    public async Task Every_write_goes_through_a_runner_that_allows_only_the_changes_list()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        repo.WriteFile("a.txt", "A\n");

        await WriterFor(repo.Path).StageAsync(["a.txt"], Ct);

        var write = Assert.Single(_calls);
        Assert.True(ChangesPolicy.IsAllowed(write.Arguments, out _));
        Assert.False(ReadOnlyPolicy.IsAllowed(write.Arguments, out _));
    }

    [Theory]
    [InlineData("status --porcelain=v2 -z")]
    [InlineData("reset --hard HEAD")]
    [InlineData("push --force origin main")]
    public async Task A_changes_runner_rejects_anything_else_before_a_process_starts(string commandLine)
    {
        var runner = new GitRunner(_temp.Path, new GitRunnerOptions { ChangesOnly = true, ReadOnly = false, GitExecutable = "no-such-git", OnCall = _calls.Enqueue });

        await Assert.ThrowsAsync<ChangesPolicyViolationException>(() => runner.RunAsync(commandLine.Split(' '), Ct));
        Assert.Empty(_calls);
    }
}
