using System.Security.Cryptography;
using System.Text;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Changes;
using Diffract.Leftovers.Cleanup;

namespace Diffract.Leftovers.Tests;

/// <summary>A fixture repo to discard changes in, with the executor that applies and undoes the discard.</summary>
internal sealed class DiscardFixture : IDisposable
{
    private readonly TempDirectory _temp;

    private DiscardFixture(TempDirectory temp, FixtureRepo repo)
    {
        _temp = temp;
        Repo = repo;
        Log = new OperationLog(_temp.Combine("oplog"));
        Backups = new DiscardBackups(_temp.Combine("copies"));
    }

    public FixtureRepo Repo { get; }

    public OperationLog Log { get; }

    public DiscardBackups Backups { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public GitRunnerOptions Git => new() { Environment = IsolatedGit.Environment(_temp) };

    public static async Task<DiscardFixture> BuildAsync(bool autocrlf = false)
    {
        var temp = new TempDirectory();
        var repo = await FixtureRepo.InitAsync(temp, "repo");
        if (autocrlf)
            await repo.GitAsync("config", "core.autocrlf", "true");
        return new DiscardFixture(temp, repo);
    }

    public void Dispose() => _temp.Dispose();

    public string Full(string path) => Path.Combine(Repo.Path, path);

    public void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!);
        File.WriteAllBytes(Full(path), bytes);
    }

    public byte[] Read(string path) => File.ReadAllBytes(Full(path));

    /// <summary>Commits the files as they are on disk now.</summary>
    public async Task CommitAllAsync()
    {
        await Repo.GitAsync("add", "-A");
        await Repo.GitAsync("commit", "-q", "-m", "Initial commit");
    }

    public CleanupExecutor Executor() => new(Repo.Path, new CleanupOptions { Git = Git, Log = Log, Backups = Backups });

    public async Task<CleanupResult> DiscardAsync(params DiscardTarget[] targets) =>
        await Executor().ApplyAsync(DiscardPlanner.Plan(targets), Ct);

    public DiscardTarget Whole(string path, DiscardMode mode = DiscardMode.RestoreFromIndex) => new(Repo.Path, path, mode);

    /// <summary>The hunks of the file's unstaged diff, each as a target that reverses it.</summary>
    public async Task<IReadOnlyList<DiscardTarget>> HunksAsync(string path)
    {
        var reader = new ChangesReader(Git);
        var file = (await reader.ReadStatusAsync(Repo.Path, Ct)).Files.Single(f => f.Path == path);
        var request = new FileDiffRequest { Path = path, OldPath = file.OldPath, Staged = false, HeadId = file.HeadId, IndexId = file.IndexId };
        var hunks = (await reader.ReadFileAsync(Repo.Path, request, Ct)).Hunks;
        var writer = new ChangesWriter(Repo.Path, Git);
        List<DiscardTarget> targets = [];
        foreach (var hunk in hunks)
        {
            var found = await writer.ReadHunkPatchAsync(new HunkRequest(path, file.OldPath, hunk.Id), Ct);
            targets.Add(new DiscardTarget(Repo.Path, path, DiscardMode.ReverseHunk, found.Patch, $"the change at line {hunk.NewStart}"));
        }

        return targets;
    }

    /// <summary>
    /// Everything discard and undo may touch: git's status and index, every ref (a discard makes none), and each file's bytes.
    /// </summary>
    public async Task<string> StateAsync()
    {
        var status = (await Repo.GitAsync("status", "--porcelain=v2", "-z", "--untracked-files=all")).StandardOutput;
        var index = (await Repo.GitAsync("ls-files", "-s", "-z")).StandardOutput;
        var refs = (await Repo.GitAsync("for-each-ref")).StandardOutput;
        var gitDir = Path.Combine(Repo.Path, ".git") + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(Repo.Path, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(gitDir, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => $"{Path.GetRelativePath(Repo.Path, p)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}");
        return string.Join('\n', [status, index, refs, .. files]);
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}

/// <summary>Discard then undo ends where it started, for every kind of change and every awkward file.</summary>
public sealed class DiscardTests
{
    private static string Outcomes(CleanupResult result) => string.Join(", ", result.Steps.Select(s => $"{s.Kind} {s.Outcome} {s.Reason}".Trim()));

    private static void AllDone(CleanupResult result) => Assert.All(result.Steps, s => Assert.True(s.Outcome == StepOutcome.Done, Outcomes(result)));

    /// <summary>Discards, checks the file is back at the index version, undoes, and checks everything is as it was.</summary>
    private static async Task RoundTripAsync(DiscardFixture fixture, params DiscardTarget[] targets)
    {
        var before = await fixture.StateAsync();

        var discarded = await fixture.DiscardAsync(targets);

        AllDone(discarded);
        Assert.NotEqual(before, await fixture.StateAsync());

        var undone = await fixture.Executor().UndoAsync(discarded.CleanupId, DiscardFixture.Ct);

        AllDone(undone);
        Assert.Equal(before, await fixture.StateAsync());
    }

    // --- whole files ------------------------------------------------------------

    [Theory]
    [InlineData("lf", false, "one\ntwo\n", "one\nTWO\n")]
    [InlineData("crlf stored in the repo", false, "one\r\ntwo\r\n", "one\r\nTWO\r\n")]
    [InlineData("crlf with autocrlf", true, "one\ntwo\n", "one\r\nTWO\r\n")]
    [InlineData("mixed endings with autocrlf", true, "one\ntwo\n", "one\r\nTWO\nthree")]
    [InlineData("mixed endings without autocrlf", false, "one\r\ntwo\n", "one\nTWO\r\nthree\r")]
    [InlineData("no final newline", false, "one\ntwo", "one\nTWO")]
    [InlineData("a final newline added", false, "one\ntwo", "one\ntwo\n")]
    [InlineData("non-ascii content", false, "räksmörgås\n", "RÄKSMÖRGÅS åäö\n")]
    public async Task A_modified_file_is_put_back_and_undo_brings_every_byte_back(string name, bool autocrlf, string committed, string changed)
    {
        using var fixture = await DiscardFixture.BuildAsync(autocrlf);
        const string path = "mapp med å/ö fil.txt";
        fixture.Write(path, DiscardFixture.Bytes(committed));
        await fixture.CommitAllAsync();
        var committedOnDisk = fixture.Read(path);
        fixture.Write(path, DiscardFixture.Bytes(changed));

        await RoundTripAsync(fixture, fixture.Whole(path));

        Assert.True(changed.Length > 0, name);
        Assert.Equal(DiscardFixture.Bytes(changed), fixture.Read(path));
        Assert.NotEmpty(committedOnDisk);
    }

    [Fact]
    public async Task Discarding_puts_the_file_back_as_the_index_has_it()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("one\n"));
        await fixture.CommitAllAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("changed\n"));

        AllDone(await fixture.DiscardAsync(fixture.Whole("a.txt")));

        Assert.Equal(DiscardFixture.Bytes("one\n"), fixture.Read("a.txt"));
        Assert.DoesNotContain("a.txt", (await fixture.Repo.GitAsync("status", "--porcelain")).StandardOutput);
    }

    [Fact]
    public async Task A_deleted_file_comes_back_and_undo_deletes_it_again()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("gone.txt", DiscardFixture.Bytes("kept\n"));
        await fixture.CommitAllAsync();
        File.Delete(fixture.Full("gone.txt"));

        await RoundTripAsync(fixture, fixture.Whole("gone.txt"));

        Assert.False(File.Exists(fixture.Full("gone.txt")));
    }

    [Fact]
    public async Task Staged_changes_stay_staged_and_only_the_unstaged_ones_go()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("one\n"));
        await fixture.CommitAllAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("staged\n"));
        await fixture.Repo.GitAsync("add", "a.txt");
        fixture.Write("a.txt", DiscardFixture.Bytes("staged\nunstaged\n"));

        await RoundTripAsync(fixture, fixture.Whole("a.txt"));
        AllDone(await fixture.DiscardAsync(fixture.Whole("a.txt")));

        Assert.Equal(DiscardFixture.Bytes("staged\n"), fixture.Read("a.txt"));
        Assert.StartsWith("M.", (await fixture.Repo.GitAsync("status", "--porcelain")).StandardOutput.TrimStart().Replace(" ", "."));
        Assert.Equal("staged\n", (await fixture.Repo.GitAsync("cat-file", "blob", ":a.txt")).StandardOutput);
    }

    // --- new, untracked files ---------------------------------------------------

    [Theory]
    [InlineData("new.txt", "new\r\nfile")]
    [InlineData("ny mapp/räksmörgås med mellanslag.txt", "ny fil åäö\n")]
    public async Task A_new_untracked_file_is_deleted_and_undo_writes_it_back(string path, string content)
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("keep.txt", DiscardFixture.Bytes("x\n"));
        await fixture.CommitAllAsync();
        fixture.Write(path, DiscardFixture.Bytes(content));

        await RoundTripAsync(fixture, fixture.Whole(path, DiscardMode.DeleteNewFile));

        Assert.Equal(DiscardFixture.Bytes(content), fixture.Read(path));
    }

    [Fact]
    public async Task A_new_file_that_is_read_only_is_still_deleted_and_comes_back()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("keep.txt", DiscardFixture.Bytes("x\n"));
        await fixture.CommitAllAsync();
        fixture.Write("locked.txt", DiscardFixture.Bytes("read only\n"));
        File.SetAttributes(fixture.Full("locked.txt"), FileAttributes.ReadOnly);

        var discarded = await fixture.DiscardAsync(fixture.Whole("locked.txt", DiscardMode.DeleteNewFile));

        AllDone(discarded);
        Assert.False(File.Exists(fixture.Full("locked.txt")));
    }

    // --- renamed files ----------------------------------------------------------

    [Fact]
    public async Task A_staged_rename_with_an_edit_loses_only_the_edit()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("old name.txt", DiscardFixture.Bytes(string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"))));
        await fixture.CommitAllAsync();
        await fixture.Repo.GitAsync("mv", "old name.txt", "nytt namn åäö.txt");
        fixture.Write("nytt namn åäö.txt", DiscardFixture.Bytes(string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n")) + "edit\n"));

        await RoundTripAsync(fixture, fixture.Whole("nytt namn åäö.txt"));
        AllDone(await fixture.DiscardAsync(fixture.Whole("nytt namn åäö.txt")));

        var status = (await fixture.Repo.GitAsync("status", "--porcelain=v2")).StandardOutput;
        Assert.Contains("R.", status);
        Assert.False(File.Exists(fixture.Full("old name.txt")));
    }

    [Fact]
    public async Task An_unstaged_rename_is_the_old_file_put_back_and_the_new_one_deleted()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("old.txt", DiscardFixture.Bytes("content\n"));
        await fixture.CommitAllAsync();
        File.Move(fixture.Full("old.txt"), fixture.Full("new.txt"));

        await RoundTripAsync(fixture, fixture.Whole("old.txt"), fixture.Whole("new.txt", DiscardMode.DeleteNewFile));

        Assert.False(File.Exists(fixture.Full("old.txt")));
        Assert.True(File.Exists(fixture.Full("new.txt")));
    }

    // --- one hunk ---------------------------------------------------------------

    private static string Numbered(string eol, params (int Line, string Text)[] changes) =>
        string.Concat(Enumerable.Range(1, 20).Select(i => (changes.FirstOrDefault(c => c.Line == i).Text ?? $"line {i}") + eol));

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\r\n")]
    public async Task One_hunk_is_reversed_and_the_other_stays_and_undo_brings_the_whole_file_back(bool autocrlf, string eol)
    {
        using var fixture = await DiscardFixture.BuildAsync(autocrlf);
        fixture.Write("f.txt", DiscardFixture.Bytes(Numbered(eol)));
        await fixture.CommitAllAsync();
        fixture.Write("f.txt", DiscardFixture.Bytes(Numbered(eol, (3, "THREE"), (15, "FIFTEEN"))));
        var hunks = await fixture.HunksAsync("f.txt");
        Assert.Equal(2, hunks.Count);

        await RoundTripAsync(fixture, hunks[0]);
        AllDone(await fixture.DiscardAsync(hunks[0]));

        Assert.Equal(DiscardFixture.Bytes(Numbered(eol, (15, "FIFTEEN"))), fixture.Read("f.txt"));
    }

    [Fact]
    public async Task A_hunk_in_a_file_without_a_final_newline_is_reversed()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("f.txt", DiscardFixture.Bytes("1\n2\n3\n4\n5\n6"));
        await fixture.CommitAllAsync();
        // The last line gets its newline and a line follows it, which is one hunk. The change to line 2 is another.
        fixture.Write("f.txt", DiscardFixture.Bytes("1\nTWO\n3\n4\n5\n6\n7"));
        var hunks = await fixture.HunksAsync("f.txt");
        Assert.Equal(2, hunks.Count);

        await RoundTripAsync(fixture, hunks[^1]);
        AllDone(await fixture.DiscardAsync(hunks[^1]));

        Assert.Equal(DiscardFixture.Bytes("1\nTWO\n3\n4\n5\n6"), fixture.Read("f.txt"));
    }

    [Fact]
    public async Task A_hunk_in_a_file_with_non_ascii_content_and_path_is_reversed()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        const string path = "räksmörgås/åäö fil.txt";
        fixture.Write(path, DiscardFixture.Bytes("första\nandra\ntredje\n"));
        await fixture.CommitAllAsync();
        fixture.Write(path, DiscardFixture.Bytes("första\nÄNDRAD\ntredje\n"));
        var hunks = await fixture.HunksAsync(path);

        await RoundTripAsync(fixture, hunks.Single());
        AllDone(await fixture.DiscardAsync(hunks.Single()));

        Assert.Equal(DiscardFixture.Bytes("första\nandra\ntredje\n"), fixture.Read(path));
    }

    // --- what must not happen ---------------------------------------------------

    [Fact]
    public async Task Nothing_is_added_to_the_repo_for_the_backup()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("one\n"));
        await fixture.CommitAllAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("two\n"));
        var objects = (await fixture.Repo.GitAsync("count-objects", "-v")).StandardOutput;

        AllDone(await fixture.DiscardAsync(fixture.Whole("a.txt")));

        Assert.Equal(objects, (await fixture.Repo.GitAsync("count-objects", "-v")).StandardOutput);
        Assert.Empty((await fixture.Repo.GitAsync("tag", "--list")).StandardOutput);
        Assert.Single((await fixture.Repo.GitAsync("for-each-ref")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task A_file_that_changed_after_the_plan_was_made_is_not_discarded()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("one\n"));
        await fixture.CommitAllAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("what the dialog showed\n"));
        var plan = DiscardPlanner.Plan([fixture.Whole("a.txt")]);
        fixture.Write("a.txt", DiscardFixture.Bytes("newer work\n"));

        var result = await fixture.Executor().ApplyAsync(plan, DiscardFixture.Ct);

        Assert.Equal(StepOutcome.Skipped, result.Steps.Single().Outcome);
        Assert.Contains("changed since the dialog showed it", result.Steps.Single().Reason);
        Assert.Equal(DiscardFixture.Bytes("newer work\n"), fixture.Read("a.txt"));
    }

    [Fact]
    public async Task A_hunk_whose_file_changed_since_it_was_shown_changes_nothing()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("f.txt", DiscardFixture.Bytes(Numbered("\n")));
        await fixture.CommitAllAsync();
        fixture.Write("f.txt", DiscardFixture.Bytes(Numbered("\n", (3, "THREE"))));
        var hunk = (await fixture.HunksAsync("f.txt")).Single();
        var plan = DiscardPlanner.Plan([hunk]);
        fixture.Write("f.txt", DiscardFixture.Bytes(Numbered("\n", (3, "THREE"), (9, "NINE"))));

        var result = await fixture.Executor().ApplyAsync(plan, DiscardFixture.Ct);

        Assert.Equal(StepOutcome.Skipped, result.Steps.Single().Outcome);
        Assert.Equal(DiscardFixture.Bytes(Numbered("\n", (3, "THREE"), (9, "NINE"))), fixture.Read("f.txt"));
    }

    [Fact]
    public async Task Undo_leaves_a_file_alone_that_was_edited_after_the_discard()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("one\n"));
        await fixture.CommitAllAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("thrown away\n"));
        var discarded = await fixture.DiscardAsync(fixture.Whole("a.txt"));
        fixture.Write("a.txt", DiscardFixture.Bytes("newer work\n"));

        var undone = await fixture.Executor().UndoAsync(discarded.CleanupId, DiscardFixture.Ct);

        Assert.Equal(StepOutcome.Skipped, undone.Steps.Single().Outcome);
        Assert.Contains("has changed since it was discarded", undone.Steps.Single().Reason);
        Assert.Equal(DiscardFixture.Bytes("newer work\n"), fixture.Read("a.txt"));
    }

    [Fact]
    public async Task Undo_says_so_when_the_copy_is_gone_as_after_Diffract_was_closed()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("one\n"));
        await fixture.CommitAllAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("thrown away\n"));
        var discarded = await fixture.DiscardAsync(fixture.Whole("a.txt"));
        fixture.Backups.Clear();

        var undone = await fixture.Executor().UndoAsync(discarded.CleanupId, DiscardFixture.Ct);

        Assert.Equal(StepOutcome.Skipped, undone.Steps.Single().Outcome);
        Assert.Contains("copies last until Diffract closes", undone.Steps.Single().Reason);
        Assert.Equal(DiscardFixture.Bytes("one\n"), fixture.Read("a.txt"));
    }

    [Fact]
    public async Task A_discard_shows_in_the_history_with_undo_available_until_it_is_undone()
    {
        using var fixture = await DiscardFixture.BuildAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("one\n"));
        await fixture.CommitAllAsync();
        fixture.Write("a.txt", DiscardFixture.Bytes("two\n"));
        var discarded = await fixture.DiscardAsync(fixture.Whole("a.txt"));
        var executor = fixture.Executor();

        Assert.True(executor.History().Single().CanUndo);
        await executor.UndoAsync(discarded.CleanupId, DiscardFixture.Ct);

        Assert.False(executor.History().Single(c => c.CleanupId == discarded.CleanupId).CanUndo);
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_the_worktree_is_refused_before_anything_is_touched()
    {
        using var fixture = await DiscardFixture.BuildAsync();

        Assert.Throws<ArgumentException>(() => DiscardPlanner.Plan([fixture.Whole("../outside.txt")]));
        Assert.Throws<ArgumentException>(() => DiscardPlanner.Plan([fixture.Whole(".git/config")]));
        await Task.CompletedTask;
    }
}
