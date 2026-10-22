using System.Collections.Concurrent;
using System.Text;
using Diffract.Fixtures;
using Diffract.Git.Changes;
using Diffract.Git.Files;
using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Changes;

/// <summary>
/// The changes reader against real fixture repos. Starting git is the expensive part on Windows, so every test also
/// checks how many git processes it took.
/// </summary>
public sealed class ChangesReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ChangesReader Reader => new(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue });

    private static FileContent Text(string text) => new(FileContentKind.Text, text);

    private static (FileContent? Old, FileContent? New) Sides(FileContent? oldSide, FileContent? newSide) => (oldSide, newSide);

    private static (FileContent? Old, FileContent? New) Sides(FileDiff diff) => (diff.Old, diff.New);

    /// <summary>A repo whose first commit holds the given files, with no other changes.</summary>
    private async Task<FixtureRepo> RepoAsync(params (string Path, string Content)[] files)
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        foreach (var (path, content) in files)
            repo.WriteFile(path, content);
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "--allow-empty", "-m", "Initial commit");
        return repo;
    }

    private async Task<ChangedFile> StatusOfAsync(FixtureRepo repo, string path) =>
        (await Reader.ReadStatusAsync(repo.Path, Ct)).Files.Single(f => f.Path == path);

    /// <summary>The diff the UI would ask for, with the ids status gave. Counts only the calls of the diff itself.</summary>
    private async Task<FileDiff> DiffAsync(FixtureRepo repo, string path, bool staged)
    {
        var file = await StatusOfAsync(repo, path);
        _calls.Clear();
        return await Reader.ReadFileAsync(repo.Path, new FileDiffRequest { Path = path, OldPath = file.OldPath, Staged = staged, HeadId = file.HeadId, IndexId = file.IndexId }, Ct);
    }

    [Fact]
    public void Refuses_a_runner_that_may_write()
    {
        Assert.Throws<ArgumentException>(() => new ChangesReader(new GitRunnerOptions { ReadOnly = false }));
    }

    [Fact]
    public async Task Status_lists_every_kind_of_change_with_one_git_call()
    {
        var repo = await RepoAsync(("modified.txt", "a\n"), ("staged.txt", "a\n"), ("both.txt", "a\n"), ("deleted.txt", "d\n"), ("old name.txt", "r\n"));
        repo.WriteFile("modified.txt", "b\n");
        repo.WriteFile("staged.txt", "b\n");
        repo.WriteFile("both.txt", "b\n");
        await repo.GitAsync("add", "staged.txt", "both.txt");
        repo.WriteFile("both.txt", "c\n");
        await repo.GitAsync("rm", "-q", "deleted.txt");
        await repo.GitAsync("mv", "old name.txt", "ny räka.txt");
        repo.WriteFile("new folder/sub folder/å ä ö.txt", "new\n");
        _calls.Clear();

        var changes = await Reader.ReadStatusAsync(repo.Path, Ct);

        Assert.Equal(
        [
            ("both.txt", FileChangeKind.Modified, FileChangeKind.Modified),
            ("deleted.txt", FileChangeKind.Deleted, null),
            ("modified.txt", null, FileChangeKind.Modified),
            ("ny räka.txt", FileChangeKind.Renamed, null),
            ("staged.txt", FileChangeKind.Modified, null),
            ("new folder/sub folder/å ä ö.txt", null, FileChangeKind.Added),
        ], changes.Files.Select(f => (f.Path, f.Staged, f.Unstaged)));
        Assert.Equal("old name.txt", changes.Files.Single(f => f.Path == "ny räka.txt").OldPath);
        Assert.Equal(("main", await repo.RevParseAsync("HEAD")), (changes.Branch, changes.HeadCommit));
        Assert.Equal(StatusParser.ChangesArguments, Assert.Single(_calls).Arguments);
    }

    [Fact]
    public async Task Status_of_a_clean_worktree_lists_nothing()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));

        Assert.Empty((await Reader.ReadStatusAsync(repo.Path, Ct)).Files);
    }

    [Fact]
    public async Task Status_lists_conflicted_files_after_a_merge_that_stopped()
    {
        var repo = await RepoAsync(("file.txt", "base\n"));
        await repo.GitAsync("switch", "-q", "-c", "other");
        await repo.CommitAsync("file.txt", "theirs\n", "Other side");
        await repo.GitAsync("switch", "-q", "main");
        await repo.CommitAsync("file.txt", "ours\n", "Main side");
        await Assert.ThrowsAsync<GitCommandException>(() => repo.GitAsync("merge", "-q", "other"));

        var file = Assert.Single((await Reader.ReadStatusAsync(repo.Path, Ct)).Files);

        Assert.Equal(("file.txt", ChangedFileKind.Conflicted), (file.Path, file.Kind));
    }

    [Fact]
    public async Task Status_of_a_linked_worktree_is_its_own()
    {
        var repo = await RepoAsync(("a.txt", "a\n"));
        var linked = _temp.Combine("linked wt");
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/åäö", linked);
        await File.WriteAllTextAsync(Path.Combine(linked, "only here.txt"), "x", Ct);

        var changes = await Reader.ReadStatusAsync(linked, Ct);

        Assert.Equal(("feature/åäö", "only here.txt"), (changes.Branch, Assert.Single(changes.Files).Path));
        Assert.Empty((await Reader.ReadStatusAsync(repo.Path, Ct)).Files);
    }

    [Fact]
    public async Task Status_before_the_first_commit()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "empty");
        repo.WriteFile("first.txt", "x");

        var changes = await Reader.ReadStatusAsync(repo.Path, Ct);

        Assert.Equal(("main", (string?)null), (changes.Branch, changes.HeadCommit));
        Assert.Equal(ChangedFileKind.Untracked, Assert.Single(changes.Files).Kind);
    }

    [Fact]
    public async Task Unstaged_diff_is_the_index_against_the_disk_with_one_git_call_and_one_for_its_hunks()
    {
        var repo = await RepoAsync(("file.txt", "one\ntwo\n"));
        repo.WriteFile("file.txt", "one\nTWO\n");

        var diff = await DiffAsync(repo, "file.txt", staged: false);

        Assert.Equal(Sides(Text("one\ntwo\n"), Text("one\nTWO\n")), Sides(diff));
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal((2, 1, 2, 1), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task Staged_diff_is_head_against_the_index_with_one_git_call_per_side_and_one_for_its_hunks()
    {
        var repo = await RepoAsync(("file.txt", "one\n"));
        repo.WriteFile("file.txt", "two\n");
        await repo.GitAsync("add", "file.txt");
        repo.WriteFile("file.txt", "three\n");

        var staged = await DiffAsync(repo, "file.txt", staged: true);
        Assert.Equal(3, _calls.Count);
        var unstaged = await DiffAsync(repo, "file.txt", staged: false);

        Assert.Equal(Sides(Text("one\n"), Text("two\n")), Sides(staged));
        Assert.Equal(Sides(Text("two\n"), Text("three\n")), Sides(unstaged));
    }

    [Fact]
    public async Task An_untracked_file_has_only_a_new_side_and_needs_no_git()
    {
        var repo = await RepoAsync();
        repo.WriteFile("new folder/räksmörgås.txt", "smörgås\n");

        var diff = await DiffAsync(repo, "new folder/räksmörgås.txt", staged: false);

        Assert.Equal(Sides(null, Text("smörgås\n")), Sides(diff));
        Assert.Empty(diff.Hunks);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task An_intent_to_add_file_has_only_a_new_side()
    {
        var repo = await RepoAsync();
        repo.WriteFile("new.txt", "new\n");
        await repo.GitAsync("add", "--intent-to-add", "new.txt");

        Assert.Equal(Sides(null, Text("new\n")), Sides(await DiffAsync(repo, "new.txt", staged: false)));
    }

    [Fact]
    public async Task Added_and_deleted_files_have_one_side()
    {
        var repo = await RepoAsync(("staged gone.txt", "a\n"), ("gone.txt", "b\n"));
        repo.WriteFile("added.txt", "new\n");
        await repo.GitAsync("add", "added.txt");
        await repo.GitAsync("rm", "-q", "staged gone.txt");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));

        Assert.Equal(Sides(null, Text("new\n")), Sides(await DiffAsync(repo, "added.txt", staged: true)));
        Assert.Equal(Sides(Text("a\n"), null), Sides(await DiffAsync(repo, "staged gone.txt", staged: true)));
        Assert.Equal(Sides(Text("b\n"), null), Sides(await DiffAsync(repo, "gone.txt", staged: false)));
    }

    [Fact]
    public async Task A_staged_rename_compares_the_old_file_with_the_new_one()
    {
        var repo = await RepoAsync(("old name.txt", "one\ntwo\nthree\nfour\n"));
        await repo.GitAsync("mv", "old name.txt", "ny räka.txt");
        repo.WriteFile("ny räka.txt", "one\ntwo\nthree\nFOUR\n");
        await repo.GitAsync("add", "ny räka.txt");

        var diff = await DiffAsync(repo, "ny räka.txt", staged: true);

        Assert.Equal(Sides(Text("one\ntwo\nthree\nfour\n"), Text("one\ntwo\nthree\nFOUR\n")), Sides(diff));
    }

    [Fact]
    public async Task With_autocrlf_the_unstaged_sides_both_have_crlf_and_the_staged_sides_both_have_lf()
    {
        var repo = await RepoAsync();
        await repo.GitAsync("config", "core.autocrlf", "true");
        repo.WriteFile("crlf.txt", "one\r\ntwo\r\n");
        await repo.GitAsync("add", "crlf.txt");
        await repo.GitAsync("commit", "-q", "-m", "Add a CRLF file");
        repo.WriteFile("crlf.txt", "one\r\nTWO\r\n");

        var unstaged = await DiffAsync(repo, "crlf.txt", staged: false);
        await repo.GitAsync("add", "crlf.txt");
        var staged = await DiffAsync(repo, "crlf.txt", staged: true);

        Assert.Equal(Sides(Text("one\r\ntwo\r\n"), Text("one\r\nTWO\r\n")), Sides(unstaged));
        Assert.Equal(Sides(Text("one\ntwo\n"), Text("one\nTWO\n")), Sides(staged));
    }

    [Fact]
    public async Task Crlf_stored_in_the_repo_keeps_its_crs_on_both_sides()
    {
        var repo = await RepoAsync(("crlf.txt", "one\r\ntwo\r\n"));
        repo.WriteFile("crlf.txt", "one\r\ntwo\r\nthree\r\n");

        var diff = await DiffAsync(repo, "crlf.txt", staged: false);

        Assert.Equal(Sides(Text("one\r\ntwo\r\n"), Text("one\r\ntwo\r\nthree\r\n")), Sides(diff));
    }

    [Fact]
    public async Task A_binary_file_on_disk_comes_without_text()
    {
        var repo = await RepoAsync();
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "image.bin"), [1, 0, 2], Ct);

        var diff = await DiffAsync(repo, "image.bin", staged: false);

        Assert.Equal(Sides(null, new FileContent(FileContentKind.Binary, null)), Sides(diff));
    }

    [Fact]
    public async Task A_file_on_disk_over_the_limit_is_not_read()
    {
        var path = _temp.Combine("large.txt");
        await File.WriteAllTextAsync(path, new string('x', 2000), Ct);

        Assert.Equal(FileContentKind.TooLarge, (await WorktreeFile.ReadAsync(path, Ct, maxBytes: 1000))!.Kind);
        Assert.Equal(FileContentKind.Text, (await WorktreeFile.ReadAsync(path, Ct, maxBytes: 2000))!.Kind);
    }

    [Fact]
    public async Task A_file_that_another_program_is_writing_can_still_be_read()
    {
        var repo = await RepoAsync();
        var path = Path.Combine(repo.Path, "open.txt");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("being written\n"), Ct);
        await writer.FlushAsync(Ct);

        var diff = await Reader.ReadFileAsync(repo.Path, new FileDiffRequest { Path = "open.txt", Staged = false }, Ct);

        Assert.Equal(Text("being written\n"), diff.New);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("./file.txt")]
    [InlineData(".git/config")]
    [InlineData(".GIT/HEAD")]
    [InlineData("sub/.git/config")]
    [InlineData("")]
    public async Task A_path_outside_the_worktree_or_in_git_is_refused_before_anything_is_read(string path)
    {
        var repo = await RepoAsync(("file.txt", "a\n"));
        _calls.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ReadFileAsync(repo.Path, new FileDiffRequest { Path = path, Staged = false }, Ct));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_rooted_path_is_refused()
    {
        var repo = await RepoAsync(("file.txt", "a\n"));
        var rooted = Path.Combine(repo.Path, "file.txt");

        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ReadFileAsync(repo.Path, new FileDiffRequest { Path = rooted, Staged = false }, Ct));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("--output=x")]
    [InlineData("abc123")]
    public async Task An_id_that_is_not_a_full_object_id_is_refused_before_git_runs(string id)
    {
        var repo = await RepoAsync(("file.txt", "a\n"));
        _calls.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ReadFileAsync(repo.Path, new FileDiffRequest { Path = "file.txt", Staged = true, HeadId = id }, Ct));
        Assert.Empty(_calls);
    }
}
