using System.Collections.Concurrent;
using Diffract.Fixtures;
using Diffract.Git.Changes;

namespace Diffract.Git.Tests.Changes;

/// <summary>Saving and applying stashes in fixture repos, with spaces and å ä ö in the paths.</summary>
public sealed class StashWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunnerOptions Options => new() { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue };

    private StashWriter WriterFor(FixtureRepo repo) => new(repo.Path, Options);

    private List<string> Writes => [.. _calls.Where(c => !ReadOnlyPolicy.IsAllowed(c.Arguments, out _)).Select(c => string.Join(' ', c.Arguments))];

    /// <summary>A repo with one commit of "räksmörgås.txt" in a folder with a space.</summary>
    private async Task<FixtureRepo> RepoAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "a repo åäö");
        await repo.CommitAsync("räksmörgås.txt", "one\n", "Initial commit");
        _calls.Clear();
        return repo;
    }

    /// <summary>Stashes a change through the writer, so the calls after it are the test's own.</summary>
    private async Task<string> StashedAsync(FixtureRepo repo, string file, string content, bool includeUntracked = false)
    {
        repo.WriteFile(file, content);
        var stash = (await WriterFor(repo).PushAsync(new StashRequest("Mine", includeUntracked), Ct)).CommitId;
        _calls.Clear();
        return stash;
    }

    private static async Task<string> StashListAsync(FixtureRepo repo) =>
        (await repo.GitAsync("stash", "list", "--format=%gs")).StandardOutput.Trim();

    private static string Read(FixtureRepo repo, string file) => File.ReadAllText(Path.Combine(repo.Path, file));

    private static bool Exists(FixtureRepo repo, string file) => File.Exists(Path.Combine(repo.Path, file));

    [Fact]
    public async Task Save_stashes_changes_with_the_first_line_of_the_message()
    {
        var repo = await RepoAsync();
        repo.WriteFile("räksmörgås.txt", "two\n");

        var saved = await WriterFor(repo).PushAsync(new StashRequest("Halvfärdigt\n\nmore words", IncludeUntracked: false), Ct);

        Assert.Equal(await repo.RevParseAsync("refs/stash"), saved.CommitId);
        Assert.Equal(("On main: Halvfärdigt", "one\n"), (await StashListAsync(repo), Read(repo, "räksmörgås.txt")));
        Assert.Equal(["stash push -m Halvfärdigt"], Writes);
    }

    [Theory]
    [InlineData("  \n", "WIP on main:", "stash push")]
    [InlineData("--all of it", "On main: --all of it", "stash push -m --all of it")]
    public async Task The_message_is_its_own_argument_and_git_names_a_stash_without_one(string message, string listed, string command)
    {
        var repo = await RepoAsync();
        repo.WriteFile("räksmörgås.txt", "two\n");

        await WriterFor(repo).PushAsync(new StashRequest(message, IncludeUntracked: false), Ct);

        Assert.StartsWith(listed, await StashListAsync(repo));
        Assert.Equal(command, Assert.Single(Writes));
    }

    [Fact]
    public async Task Save_with_new_files_takes_them_too()
    {
        var repo = await RepoAsync();
        repo.WriteFile("ny fil.txt", "new\n");

        await WriterFor(repo).PushAsync(new StashRequest("New file", IncludeUntracked: true), Ct);

        Assert.Equal(("", false), ((await repo.GitAsync("status", "--porcelain")).StandardOutput, Exists(repo, "ny fil.txt")));
        Assert.Equal(["stash push --include-untracked -m New file"], Writes);
    }

    [Fact]
    public async Task Only_new_files_without_include_new_files_is_nothing_to_stash()
    {
        var repo = await RepoAsync();
        repo.WriteFile("ny fil.txt", "new\n");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => WriterFor(repo).PushAsync(new StashRequest("x", IncludeUntracked: false), Ct));

        Assert.Contains("Include new files", e.Message);
        Assert.Equal((true, ""), (Exists(repo, "ny fil.txt"), await StashListAsync(repo)));
    }

    [Fact]
    public async Task A_clean_worktree_has_nothing_to_stash_even_with_stashes_already_there()
    {
        var repo = await RepoAsync();
        await StashedAsync(repo, "räksmörgås.txt", "two\n");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => WriterFor(repo).PushAsync(new StashRequest("x", IncludeUntracked: true), Ct));

        Assert.Equal("There is nothing to stash.", e.Message);
    }

    [Fact]
    public async Task Apply_by_commit_applies_the_right_stash_after_another_moved_it_down()
    {
        var repo = await RepoAsync();
        repo.WriteFile("ny fil.txt", "new\n");
        var mine = await StashedAsync(repo, "räksmörgås.txt", "mine\n", includeUntracked: true);
        repo.WriteFile("räksmörgås.txt", "an agent's\n");
        await repo.GitAsync("stash", "push", "-m", "An agent's");
        _calls.Clear();

        var applied = await WriterFor(repo).ApplyAsync(mine, Ct);

        Assert.Equal((false, "mine\n", "new\n"), (applied.Conflicts, Read(repo, "räksmörgås.txt"), Read(repo, "ny fil.txt")));
        Assert.Equal("On main: An agent's\nOn main: Mine", await StashListAsync(repo));
        Assert.Equal([$"stash apply {mine}"], Writes);
    }

    [Fact]
    public async Task An_apply_that_conflicts_keeps_the_stash_and_leaves_the_file_conflicted()
    {
        var repo = await RepoAsync();
        var stash = await StashedAsync(repo, "räksmörgås.txt", "stashed\n");
        await repo.CommitAsync("räksmörgås.txt", "committed since\n", "Change it");

        var applied = await WriterFor(repo).ApplyAsync(stash, Ct);

        Assert.Equal((true, "On main: Mine"), (applied.Conflicts, await StashListAsync(repo)));
        Assert.Contains("still in the list", applied.Message);
        Assert.StartsWith("UU ", (await repo.GitAsync("status", "--porcelain")).StandardOutput);
    }

    /// <summary>A change in the way, which git refuses with exit code 1, and a new file in the way, refused with 128.</summary>
    [Theory]
    [InlineData("räksmörgås.txt", false, "Your local changes")]
    [InlineData("ny fil.txt", true, "already exists")]
    public async Task An_apply_that_git_refuses_says_why_and_changes_nothing(string file, bool isNew, string gitSays)
    {
        var repo = await RepoAsync();
        var stash = await StashedAsync(repo, file, "stashed\n", includeUntracked: isNew);
        repo.WriteFile(file, "made since\n");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => WriterFor(repo).ApplyAsync(stash, Ct));

        Assert.StartsWith("Git did not apply the stash", e.Message);
        Assert.Contains(gitSays, e.GitOutput);
        Assert.Equal(("made since\n", "On main: Mine"), (Read(repo, file), await StashListAsync(repo)));
    }

    [Fact]
    public async Task Apply_takes_only_a_full_commit_id()
    {
        var repo = await RepoAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo).ApplyAsync("stash@{0}", Ct));

        Assert.Empty(Writes);
    }
}
