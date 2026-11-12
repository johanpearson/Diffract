using System.Text;
using Diffract.Git;
using Diffract.Git.Refs;
using Diffract.Leftovers.Cleanup;

namespace Diffract.Leftovers.Tests;

/// <summary>
/// Hard resets in fixture repos. main has three commits that each change a.txt; the tests leave uncommitted work
/// in several shapes, reset to the first commit, and undo.
/// </summary>
public sealed class HardResetTests : IDisposable
{
    private static readonly string[] Files = ["a.txt", "staged.txt", "both.txt", "new.txt", "untracked.txt", "dir/deep.txt", "crlf.txt"];

    private DiscardFixture? _fixture;

    private static CancellationToken Ct => DiscardFixture.Ct;

    public void Dispose() => _fixture?.Dispose();

    private sealed record Repo(DiscardFixture Fixture, string First, string Third);

    private async Task<Repo> RepoAsync(bool dirty = true)
    {
        var fixture = _fixture = await DiscardFixture.BuildAsync();
        var repo = fixture.Repo;
        await repo.CommitAsync("both.txt", "base\n", "Base");
        await repo.CommitAsync("staged.txt", "base\n", "Staged base");
        var first = await repo.CommitAsync("a.txt", "1\n", "First");
        await repo.CommitAsync("a.txt", "2\n", "Second");
        var third = await repo.CommitAsync("a.txt", "3\n", "Third");
        if (dirty)
            await MakeDirtyAsync(fixture);
        return new Repo(fixture, first, third);
    }

    /// <summary>An unstaged edit, a staged edit, a staged edit with another on top, a staged new file, an untracked one, and one with CRLF.</summary>
    private static async Task MakeDirtyAsync(DiscardFixture f)
    {
        f.Write("a.txt", "unstaged edit\n"u8.ToArray());
        f.Write("staged.txt", "staged edit\n"u8.ToArray());
        f.Write("both.txt", "staged part\n"u8.ToArray());
        await f.Repo.GitAsync("add", "staged.txt", "both.txt");
        f.Write("both.txt", "staged part\nand then unstaged\n"u8.ToArray());
        f.Write("new.txt", "staged new file\n"u8.ToArray());
        await f.Repo.GitAsync("add", "new.txt");
        f.Write("untracked.txt", "untracked\n"u8.ToArray());
        f.Write("dir/deep.txt", "in a folder\n"u8.ToArray());
        f.Write("crlf.txt", "one\r\nTWO\nthree"u8.ToArray());
    }

    private HardReset ResetFor(Repo repo) => new(repo.Fixture.Repo.Path, repo.Fixture.Git, repo.Fixture.Backups);

    /// <summary>HEAD, what git says about staged and unstaged files, the staged object ids, and every file's bytes.</summary>
    private static async Task<string> StateAsync(Repo repo)
    {
        var f = repo.Fixture;
        var files = Files.Select(p => File.Exists(f.Full(p)) ? $"{p}={Convert.ToHexString(f.Read(p))}" : $"{p}=absent");
        return string.Join("\n", [
            await f.Repo.RevParseAsync("HEAD"),
            (await f.Repo.GitAsync("status", "--porcelain=v1", "-uall")).StandardOutput,
            (await f.Repo.GitAsync("ls-files", "-s")).StandardOutput,
            .. files,
        ]);
    }

    [Fact]
    public async Task A_hard_reset_throws_the_uncommitted_work_away_and_moves_the_branch()
    {
        var repo = await RepoAsync();

        var reset = await ResetFor(repo).ResetAsync(repo.First, Ct);

        Assert.Equal((ResetMode.Hard, "main", repo.Third, repo.First), (reset.Mode, reset.Branch, reset.OldCommit, reset.NewCommit));
        var f = repo.Fixture;
        Assert.Equal(repo.First, await f.Repo.RevParseAsync("main"));
        Assert.Equal("?? crlf.txt\n?? dir/deep.txt\n?? untracked.txt\n", (await f.Repo.GitAsync("status", "--porcelain=v1", "-uall")).StandardOutput);
        Assert.Equal(("1\n", false), (Encoding.UTF8.GetString(f.Read("a.txt")), File.Exists(f.Full("new.txt"))));
    }

    [Fact]
    public async Task Hard_reset_then_undo_ends_where_it_started()
    {
        var repo = await RepoAsync();
        var before = await StateAsync(repo);
        var writer = ResetFor(repo);

        var undone = await writer.UndoAsync(await writer.ResetAsync(repo.First, Ct), Ct);

        Assert.Equal(before, await StateAsync(repo));
        Assert.Equal((repo.First, repo.Third, ResetMode.Hard), (undone.OldCommit, undone.NewCommit, undone.Mode));
        Assert.Null(undone.Kept);
    }

    [Fact]
    public async Task Undo_brings_back_a_file_with_staged_and_unstaged_changes_exactly()
    {
        var repo = await RepoAsync();
        var writer = ResetFor(repo);

        await writer.UndoAsync(await writer.ResetAsync(repo.First, Ct), Ct);

        var f = repo.Fixture;
        var statuses = (await f.Repo.GitAsync("status", "--porcelain=v1", "--", "both.txt", "new.txt")).StandardOutput;
        Assert.Equal("MM both.txt\nA  new.txt\n", statuses);
        Assert.Equal("staged part\n", (await f.Repo.GitAsync("show", ":both.txt")).StandardOutput);
        Assert.Equal("one\r\nTWO\nthree"u8.ToArray(), f.Read("crlf.txt"));
    }

    [Fact]
    public async Task Only_files_the_reset_changed_are_copied()
    {
        var repo = await RepoAsync();

        var reset = await ResetFor(repo).ResetAsync(repo.First, Ct);

        var paths = reset.Kept!.Files.Select(k => k.Path).Order().ToList();
        Assert.Equal(["a.txt", "both.txt", "new.txt", "staged.txt"], paths);
        Assert.Equal(paths.Count, Directory.GetFiles(repo.Fixture.Backups.Folder).Length);
    }

    [Fact]
    public async Task An_untracked_file_the_target_commit_would_overwrite_is_kept()
    {
        var repo = await RepoAsync(dirty: false);
        await repo.Fixture.Repo.GitAsync("rm", "-q", "--cached", "staged.txt");
        await repo.Fixture.Repo.GitAsync("commit", "-q", "-m", "Stop tracking staged.txt");
        repo.Fixture.Write("staged.txt", "my own file\n"u8.ToArray());
        var writer = ResetFor(repo);
        var back = await repo.Fixture.Repo.RevParseAsync("HEAD~1");

        var reset = await writer.ResetAsync(back, Ct);
        Assert.Equal("base\n", Encoding.UTF8.GetString(repo.Fixture.Read("staged.txt")));
        await writer.UndoAsync(reset, Ct);

        Assert.Equal("my own file\n", Encoding.UTF8.GetString(repo.Fixture.Read("staged.txt")));
    }

    [Fact]
    public async Task A_clean_worktree_keeps_nothing_and_still_undoes()
    {
        var repo = await RepoAsync(dirty: false);
        var before = await StateAsync(repo);
        var writer = ResetFor(repo);

        var reset = await writer.ResetAsync(repo.First, Ct);
        await writer.UndoAsync(reset, Ct);

        Assert.Empty(reset.Kept!.Files);
        Assert.Empty(reset.Kept.Staged);
        Assert.Equal(before, await StateAsync(repo));
    }

    [Fact]
    public async Task Undo_does_nothing_once_the_branch_has_moved_on()
    {
        var repo = await RepoAsync();
        var writer = ResetFor(repo);
        var reset = await writer.ResetAsync(repo.First, Ct);
        var later = await repo.Fixture.Repo.CommitAsync("c.txt", "c\n", "Later");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => writer.UndoAsync(reset, Ct));

        Assert.Equal("main is no longer where the reset left it, so undo did nothing. Something has changed it since.", e.Message);
        Assert.Equal(later, await repo.Fixture.Repo.RevParseAsync("main"));
    }

    [Fact]
    public async Task Undo_does_nothing_when_a_kept_file_has_changed_since()
    {
        var repo = await RepoAsync();
        var writer = ResetFor(repo);
        var reset = await writer.ResetAsync(repo.First, Ct);
        repo.Fixture.Write("a.txt", "newer work\n"u8.ToArray());

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => writer.UndoAsync(reset, Ct));

        Assert.Equal("a.txt has changed since the reset, so undo did nothing.", e.Message);
        Assert.Equal(repo.First, await repo.Fixture.Repo.RevParseAsync("main"));
        Assert.Equal(("newer work\n", false), (Encoding.UTF8.GetString(repo.Fixture.Read("a.txt")), File.Exists(repo.Fixture.Full("new.txt"))));
    }

    [Fact]
    public async Task Undo_says_so_when_the_copies_are_gone()
    {
        var repo = await RepoAsync();
        var writer = ResetFor(repo);
        var reset = await writer.ResetAsync(repo.First, Ct);
        repo.Fixture.Backups.Clear();

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => writer.UndoAsync(reset, Ct));

        Assert.Contains("copies last until Diffract closes", e.Message);
        Assert.Equal(repo.First, await repo.Fixture.Repo.RevParseAsync("main"));
    }

    [Fact]
    public async Task A_stopped_merge_is_not_reset()
    {
        var repo = await RepoAsync(dirty: false);
        var f = repo.Fixture;
        await f.Repo.GitAsync("switch", "-q", "-c", "other", repo.First);
        await f.Repo.CommitAsync("a.txt", "other\n", "Other");
        await f.Repo.GitAsync("switch", "-q", "main");
        await Assert.ThrowsAsync<GitCommandException>(() => f.Repo.GitAsync("merge", "other"));
        var before = await f.Repo.RevParseAsync("HEAD");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => ResetFor(repo).ResetAsync(repo.First, Ct));

        Assert.StartsWith("A merge, rebase or cherry-pick is stopped on a conflict here.", e.Message);
        Assert.Equal(before, await f.Repo.RevParseAsync("HEAD"));
    }

    [Fact]
    public async Task A_refused_reset_leaves_no_copies_behind()
    {
        var repo = await RepoAsync();
        await repo.Fixture.Repo.GitAsync("switch", "-q", "--detach");

        await Assert.ThrowsAsync<ChangeRefusedException>(() => ResetFor(repo).ResetAsync(repo.First, Ct));

        Assert.False(Directory.Exists(repo.Fixture.Backups.Folder));
    }

    [Fact]
    public async Task Only_a_full_commit_id_is_taken() =>
        await Assert.ThrowsAsync<ArgumentException>(async () => await ResetFor(await RepoAsync()).ResetAsync("HEAD~1", Ct));
}
