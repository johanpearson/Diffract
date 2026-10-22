using System.Collections.Concurrent;
using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Parsing;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// changes.status and changes.file over the real host, against a fixture repo with a linked worktree. Tests that count
/// git processes make their changes before the repo is opened, so no live refresh mixes in. The live tests change
/// the repo while it is open, with real file events and real time, and count nothing.
/// </summary>
public sealed class ChangesMethodsTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;
    private int _nextId = 1;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _methods.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// A repo with one commit and a linked worktree, changed by <paramref name="prepare"/>, then opened through the
    /// host, its scan done.
    /// </summary>
    private async Task<(FixtureRepo Repo, string Linked)> OpenAsync(Func<FixtureRepo, string, Task>? prepare = null)
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("readme.md", "hello\n", "Initial commit");
        var linked = _temp.Combine("linked wt åäö");
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/åäö", linked);
        if (prepare is not null)
            await prepare(repo, linked);

        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue },
            new SidecarDependencies
            {
                Notify = (method, parameters) => _host.Host.NotifyAsync(method, parameters),
                Running = new FakeProcessTable(),
            });
        _host = new HostHarness(_methods.Dispatch);
        Assert.True((await RequestAsync("repo.open", new { path = repo.Path })).TryGetProperty("result", out _));
        // The scan after opening runs git too, and starts the watcher. Let it finish before counting calls.
        Assert.Equal("Scanned", (await _host.NextJsonAsync()).GetProperty("params").GetProperty("changes")[0].GetProperty("kind").GetString());
        _calls.Clear();
        return (repo, linked);
    }

    /// <summary>Sends a request and returns its response, skipping any repo.changed that arrives first.</summary>
    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = _nextId++;
        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        while (true)
        {
            var message = await _host.NextJsonAsync();
            if (message.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                return message;
        }
    }

    private static JsonElement Result(JsonElement response) =>
        response.TryGetProperty("result", out var result) ? result : throw new InvalidOperationException(response.ToString());

    private static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    private async Task<JsonElement> StatusAsync(string worktree) =>
        Result(await RequestAsync("changes.status", new { repoId = "1", worktree }));

    private static JsonElement FileIn(JsonElement status, string path) =>
        status.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == path);

    /// <summary>Waits for a repo.changed that names the worktree, as the UI does before it reads status again.</summary>
    private async Task WaitForChangeAsync(string worktree)
    {
        while (true)
        {
            var message = await _host.NextJsonAsync();
            if (message.GetProperty("method").GetString() == "repo.changed"
                && message.GetProperty("params").GetProperty("changes").EnumerateArray()
                    .Any(c => string.Equals(c.GetProperty("path").GetString(), worktree, StringComparison.OrdinalIgnoreCase)))
                return;
        }
    }

    [Fact]
    public async Task Status_lists_the_worktrees_files_and_branch_with_one_git_call()
    {
        var (repo, _) = await OpenAsync((repo, _) =>
        {
            repo.WriteFile("new folder/räka.txt", "new\n");
            repo.WriteFile("readme.md", "changed\n");
            return Task.CompletedTask;
        });

        var status = await StatusAsync(repo.Path);

        Assert.Equal("main", status.GetProperty("branch").GetString());
        Assert.Equal(repo.Path, status.GetProperty("worktree").GetString(), ignoreCase: true);
        Assert.Equal(["readme.md", "new folder/räka.txt"], status.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        Assert.Equal(("Untracked", "Added"), (FileIn(status, "new folder/räka.txt").GetProperty("kind").GetString(), FileIn(status, "new folder/räka.txt").GetProperty("unstaged").GetString()));
        Assert.Equal(StatusParser.ChangesArguments, Assert.Single(_calls).Arguments);
    }

    [Fact]
    public async Task Status_of_a_linked_worktree_is_read_there()
    {
        var (_, linked) = await OpenAsync((_, linked) => File.WriteAllTextAsync(Path.Combine(linked, "only here.txt"), "x", Ct));

        var status = await StatusAsync(linked + Path.DirectorySeparatorChar);

        Assert.Equal("feature/åäö", status.GetProperty("branch").GetString());
        Assert.Equal(linked, status.GetProperty("worktree").GetString(), ignoreCase: true);
        Assert.Equal("only here.txt", Assert.Single(status.GetProperty("files").EnumerateArray().ToList()).GetProperty("path").GetString());
    }

    [Fact]
    public async Task File_answers_both_sides_of_a_staged_and_an_unstaged_diff()
    {
        var (repo, _) = await OpenAsync(async (repo, _) =>
        {
            repo.WriteFile("readme.md", "staged\n");
            await repo.GitAsync("add", "readme.md");
            repo.WriteFile("readme.md", "unstaged\n");
        });
        var file = FileIn(await StatusAsync(repo.Path), "readme.md");
        _calls.Clear();

        var staged = Result(await RequestAsync("changes.file", Params(repo.Path, file, staged: true)));
        var unstaged = Result(await RequestAsync("changes.file", Params(repo.Path, file, staged: false)));

        Assert.Equal(("hello\n", "staged\n"), (staged.GetProperty("old").GetProperty("text").GetString(), staged.GetProperty("new").GetProperty("text").GetString()));
        Assert.Equal(("staged\n", "unstaged\n"), (unstaged.GetProperty("old").GetProperty("text").GetString(), unstaged.GetProperty("new").GetProperty("text").GetString()));
        Assert.Equal(1, staged.GetProperty("hunks").GetArrayLength());
        Assert.Equal(1, unstaged.GetProperty("hunks").GetArrayLength());
        Assert.Equal(5, _calls.Count);
    }

    [Fact]
    public async Task A_new_file_has_no_old_side()
    {
        var (repo, _) = await OpenAsync((repo, _) =>
        {
            repo.WriteFile("new.txt", "new\n");
            return Task.CompletedTask;
        });
        var file = FileIn(await StatusAsync(repo.Path), "new.txt");

        var diff = Result(await RequestAsync("changes.file", Params(repo.Path, file, staged: false)));

        Assert.Equal(JsonValueKind.Null, diff.GetProperty("old").ValueKind);
        Assert.Equal("new\n", diff.GetProperty("new").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_folder_that_is_not_one_of_the_repos_worktrees_is_refused_without_running_git()
    {
        FixtureRepo other = null!;
        var (repo, _) = await OpenAsync(async (_, _) => other = await FixtureRepo.InitAsync(_temp, "other"));

        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.status", new { repoId = "1", worktree = other.Path })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.status", new { repoId = "1", worktree = "repo" })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.status", new { repoId = "1", worktree = Path.Combine(repo.Path, "src") })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.status", new { repoId = "1" })));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_path_outside_the_worktree_or_an_id_that_is_not_one_is_refused_without_running_git()
    {
        var (repo, _) = await OpenAsync();

        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.file",
            new { repoId = "1", worktree = repo.Path, path = "../other/secret.txt", staged = false })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.file",
            new { repoId = "1", worktree = repo.Path, path = "readme.md", staged = true, headId = "HEAD" })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.file",
            new { repoId = "1", worktree = repo.Path, staged = false })));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_new_file_shows_in_status_after_its_repo_changed()
    {
        var (repo, _) = await OpenAsync();

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "notes.txt"), "todo", Ct);
        await WaitForChangeAsync(repo.Path);

        Assert.Equal("Added", FileIn(await StatusAsync(repo.Path), "notes.txt").GetProperty("unstaged").GetString());
    }

    [Fact]
    public async Task A_file_staged_by_another_git_shows_as_staged_after_its_repo_changed()
    {
        var (repo, linked) = await OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(linked, "agent.txt"), "work", Ct);
        await WaitForChangeAsync(linked);

        await repo.At(linked).GitAsync("add", "agent.txt");
        await WaitForChangeAsync(linked);

        var file = FileIn(await StatusAsync(linked), "agent.txt");
        Assert.Equal(("Added", JsonValueKind.Null), (file.GetProperty("staged").GetString(), file.GetProperty("unstaged").ValueKind));
    }

    // --- staging -------------------------------------------------------------------------
    // Staging changes the index, which the watcher sees, so these tests count no git calls. ChangesWriterTests do.

    [Fact]
    public async Task Stage_and_unstage_files_answer_with_the_new_status()
    {
        var (repo, _) = await OpenAsync((repo, _) =>
        {
            repo.WriteFile("readme.md", "changed\n");
            repo.WriteFile("new folder/ny fil.txt", "new\n");
            return Task.CompletedTask;
        });

        var staged = Result(await RequestAsync("changes.stage", new { repoId = "1", worktree = repo.Path, paths = new[] { "readme.md", "new folder/ny fil.txt" } }));
        Assert.All(staged.GetProperty("files").EnumerateArray(), f => Assert.Equal(JsonValueKind.Null, f.GetProperty("unstaged").ValueKind));

        var unstaged = Result(await RequestAsync("changes.unstage", new { repoId = "1", worktree = repo.Path, paths = new[] { "readme.md" } }));
        Assert.Equal(("Modified", JsonValueKind.Null), (FileIn(unstaged, "readme.md").GetProperty("unstaged").GetString(), FileIn(unstaged, "readme.md").GetProperty("staged").ValueKind));
        Assert.Equal("Added", FileIn(unstaged, "new folder/ny fil.txt").GetProperty("staged").GetString());
    }

    [Fact]
    public async Task Stage_a_hunk_from_the_diff_the_ui_was_shown()
    {
        var (repo, _) = await OpenAsync((repo, _) =>
        {
            repo.WriteFile("readme.md", "HELLO\n");
            return Task.CompletedTask;
        });
        var file = FileIn(await StatusAsync(repo.Path), "readme.md");
        var hunk = Result(await RequestAsync("changes.file", Params(repo.Path, file, staged: false))).GetProperty("hunks")[0];

        var status = Result(await RequestAsync("changes.stage", new
        {
            repoId = "1",
            worktree = repo.Path,
            hunk = new { path = "readme.md", id = hunk.GetProperty("id").GetString() },
        }));

        Assert.Equal(("Modified", JsonValueKind.Null), (FileIn(status, "readme.md").GetProperty("staged").GetString(), FileIn(status, "readme.md").GetProperty("unstaged").ValueKind));
    }

    [Fact]
    public async Task Every_git_call_is_a_read_or_on_the_changes_list_and_writes_are_never_reads()
    {
        var (repo, _) = await OpenAsync((repo, _) =>
        {
            repo.WriteFile("readme.md", "HELLO\n");
            return Task.CompletedTask;
        });

        await RequestAsync("changes.stage", new { repoId = "1", worktree = repo.Path, paths = new[] { "readme.md" } });
        await RequestAsync("changes.unstage", new { repoId = "1", worktree = repo.Path, paths = new[] { "readme.md" } });

        Assert.All(_calls, c => Assert.True(ReadOnlyPolicy.IsAllowed(c.Arguments, out _) || ChangesPolicy.IsAllowed(c.Arguments, out _), string.Join(' ', c.Arguments)));
        Assert.Equal(["add", "restore"], _calls.Where(c => ChangesPolicy.IsAllowed(c.Arguments, out _)).Select(c => c.Arguments[0]));
    }

    [Fact]
    public async Task A_hunk_that_changed_since_it_was_shown_is_a_changed_since_shown_error()
    {
        var (repo, _) = await OpenAsync((repo, _) =>
        {
            repo.WriteFile("readme.md", "HELLO\n");
            return Task.CompletedTask;
        });

        var response = await RequestAsync("changes.stage", new { repoId = "1", worktree = repo.Path, hunk = new { path = "readme.md", id = "0123456789abcdef0123456789abcdef" } });

        Assert.Equal("ChangedSinceShown", response.GetProperty("error").GetProperty("data").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_locked_index_is_an_index_locked_error_and_the_lock_stays()
    {
        string lockFile = null!;
        var (repo, _) = await OpenAsync(async (repo, _) =>
        {
            repo.WriteFile("readme.md", "HELLO\n");
            lockFile = Path.Combine(repo.Path, ".git", "index.lock");
            await File.WriteAllTextAsync(lockFile, "", Ct);
        });

        var response = await RequestAsync("changes.stage", new { repoId = "1", worktree = repo.Path, paths = new[] { "readme.md" } });

        var error = response.GetProperty("error");
        Assert.Equal("IndexLocked", error.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("Another git process is using this repo, probably an agent. Try again in a moment.", error.GetProperty("message").GetString());
        Assert.True(File.Exists(lockFile));
    }

    [Fact]
    public async Task Paths_and_a_hunk_together_neither_or_a_nested_repo_are_refused()
    {
        var (repo, linked) = await OpenAsync(async (repo, _) =>
            await repo.GitAsync("worktree", "add", "-q", "-b", "claude/task", Path.Combine(repo.Path, ".claude", "worktrees", "task")));
        var hunk = new { path = "readme.md", id = "0123" };

        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.stage", new { repoId = "1", worktree = repo.Path, paths = new[] { "readme.md" }, hunk })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.stage", new { repoId = "1", worktree = repo.Path })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.stage", new { repoId = "1", worktree = repo.Path, paths = new[] { ".claude/worktrees/task" } })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.unstage", new { repoId = "1", worktree = repo.Path, hunk = new { path = "readme.md" } })));
        Assert.Equal(RpcErrorCodes.InvalidParams, ErrorCode(await RequestAsync("changes.stage", new { repoId = "1", worktree = linked, paths = Array.Empty<string>() })));
    }

    private static object Params(string worktree, JsonElement file, bool staged) => new
    {
        repoId = "1",
        worktree,
        path = file.GetProperty("path").GetString(),
        oldPath = file.GetProperty("oldPath").GetString(),
        staged,
        headId = file.GetProperty("headId").GetString(),
        indexId = file.GetProperty("indexId").GetString(),
    };
}
