using System.Collections.Concurrent;
using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Parsing;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// changes.status and changes.file over the real host, against a fixture repo with a linked worktree. Every test also
/// checks how many git processes it took. The live tests use real file events and real time.
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

    /// <summary>A repo with one commit and a linked worktree, opened through the host, its scan done.</summary>
    private async Task<(FixtureRepo Repo, string Linked)> OpenAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("readme.md", "hello\n", "Initial commit");
        var linked = _temp.Combine("linked wt åäö");
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/åäö", linked);

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
        var (repo, _) = await OpenAsync();
        repo.WriteFile("new folder/räka.txt", "new\n");
        repo.WriteFile("readme.md", "changed\n");

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
        var (_, linked) = await OpenAsync();
        await File.WriteAllTextAsync(Path.Combine(linked, "only here.txt"), "x", Ct);

        var status = await StatusAsync(linked + Path.DirectorySeparatorChar);

        Assert.Equal("feature/åäö", status.GetProperty("branch").GetString());
        Assert.Equal(linked, status.GetProperty("worktree").GetString(), ignoreCase: true);
        Assert.Equal("only here.txt", Assert.Single(status.GetProperty("files").EnumerateArray().ToList()).GetProperty("path").GetString());
    }

    [Fact]
    public async Task File_answers_both_sides_of_a_staged_and_an_unstaged_diff()
    {
        var (repo, _) = await OpenAsync();
        repo.WriteFile("readme.md", "staged\n");
        await repo.GitAsync("add", "readme.md");
        repo.WriteFile("readme.md", "unstaged\n");
        var file = FileIn(await StatusAsync(repo.Path), "readme.md");
        _calls.Clear();

        var staged = Result(await RequestAsync("changes.file", Params(repo.Path, file, staged: true)));
        var unstaged = Result(await RequestAsync("changes.file", Params(repo.Path, file, staged: false)));

        Assert.Equal(("hello\n", "staged\n"), (staged.GetProperty("old").GetProperty("text").GetString(), staged.GetProperty("new").GetProperty("text").GetString()));
        Assert.Equal(("staged\n", "unstaged\n"), (unstaged.GetProperty("old").GetProperty("text").GetString(), unstaged.GetProperty("new").GetProperty("text").GetString()));
        Assert.Equal(3, _calls.Count);
    }

    [Fact]
    public async Task A_new_file_has_no_old_side()
    {
        var (repo, _) = await OpenAsync();
        repo.WriteFile("new.txt", "new\n");
        var file = FileIn(await StatusAsync(repo.Path), "new.txt");

        var diff = Result(await RequestAsync("changes.file", Params(repo.Path, file, staged: false)));

        Assert.Equal(JsonValueKind.Null, diff.GetProperty("old").ValueKind);
        Assert.Equal("new\n", diff.GetProperty("new").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_folder_that_is_not_one_of_the_repos_worktrees_is_refused_without_running_git()
    {
        var (repo, _) = await OpenAsync();
        var other = await FixtureRepo.InitAsync(_temp, "other");

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

    private static object Params(string worktree, JsonElement file, bool staged) => new
    {
        repoId = "1",
        worktree,
        path = file.GetProperty("path").GetString(),
        staged,
        headId = file.GetProperty("headId").GetString(),
        indexId = file.GetProperty("indexId").GetString(),
    };
}
