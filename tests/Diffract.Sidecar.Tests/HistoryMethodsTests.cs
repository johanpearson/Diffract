using System.Collections.Concurrent;
using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// history.read, history.refs, history.details and history.file over the real host, against a fixture repo that is
/// complete before it is opened, so no live refresh mixes in. Every test also checks how many git processes it took.
/// </summary>
public sealed class HistoryMethodsTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;
    private int _nextId = 1;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _methods.Dispose();
        _temp.Dispose();
    }

    /// <summary>A repo with a merge, a tag, a remote branch, a checkpoint ref, binary and text files, opened through the host.</summary>
    private async Task<FixtureRepo> OpenAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("readme.md", "hello\n", "Initial commit");
        await repo.GitAsync("switch", "-q", "-c", "feature/åäö");
        await repo.CommitAsync("readme.md", "hello\nworld\n", "Change the readme");
        await repo.GitAsync("update-ref", "refs/remotes/origin/feature/åäö", "HEAD");
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "image.bin"), [1, 0, 2], TestContext.Current.CancellationToken);
        await repo.GitAsync("add", "image.bin");
        await repo.GitAsync("commit", "-q", "-m", "Add an image");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("merge", "-q", "--no-ff", "-m", "Merge feature", "feature/åäö");
        await repo.GitAsync("tag", "-a", "v1.0", "-m", "Release");
        await repo.GitAsync("update-ref", "refs/copilot/checkpoints/session/1", "HEAD~1");

        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue },
            new SidecarDependencies
            {
                Notify = (method, parameters) => _host.Host.NotifyAsync(method, parameters),
                Running = new FakeProcessTable(),
            });
        _host = new HostHarness(_methods.Dispatch);
        var opened = await RequestAsync("repo.open", new { path = repo.Path });
        Assert.True(opened.TryGetProperty("result", out _), opened.ToString());
        // The scan after opening runs git too; let it finish, and take its repo.changed, before counting calls.
        Assert.Equal("Scanned", (await _host.NextJsonAsync()).GetProperty("params").GetProperty("changes")[0].GetProperty("kind").GetString());
        _calls.Clear();
        return repo;
    }

    /// <summary>Sends a request and returns the next line, which for every method but history.read is its response.</summary>
    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        Send(method, parameters);
        return await _host.NextJsonAsync();
    }

    private int Send(string method, object parameters)
    {
        var id = _nextId++;
        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        return id;
    }

    private static JsonElement Result(JsonElement response) =>
        response.TryGetProperty("result", out var result) ? result : throw new InvalidOperationException(response.ToString());

    private async Task<JsonElement> DetailsAsync(string subject)
    {
        var commit = (await ReadHistoryAsync()).Single(c => c.GetProperty("subject").GetString() == subject);
        return Result(await RequestAsync("history.details", new { repoId = "1", commitId = commit.GetProperty("id").GetString() }));
    }

    /// <summary>Every commit of one history.read, after checking its pages come in order and before its result.</summary>
    private async Task<List<JsonElement>> ReadHistoryAsync()
    {
        var id = Send("history.read", new { repoId = "1", token = "read-1" });
        List<JsonElement> commits = [];
        var pages = 0;
        while (true)
        {
            var message = await _host.NextJsonAsync();
            if (message.TryGetProperty("id", out var answered))
            {
                Assert.Equal(id, answered.GetInt32());
                var result = Result(message);
                Assert.Equal(commits.Count, result.GetProperty("commits").GetInt32());
                Assert.Equal(pages, result.GetProperty("pages").GetInt32());
                return commits;
            }

            Assert.Equal("history.page", message.GetProperty("method").GetString());
            var page = message.GetProperty("params");
            Assert.Equal("read-1", page.GetProperty("token").GetString());
            Assert.Equal(pages++, page.GetProperty("index").GetInt32());
            commits.AddRange(page.GetProperty("commits").EnumerateArray());
        }
    }

    [Fact]
    public async Task Read_sends_every_commit_in_pages_before_its_result_with_one_git_call()
    {
        await OpenAsync();

        var commits = await ReadHistoryAsync();

        Assert.Equal(["Merge feature", "Add an image", "Change the readme", "Initial commit"], commits.Select(c => c.GetProperty("subject").GetString()));
        var merge = commits[0];
        Assert.Equal(2, merge.GetProperty("parentIds").GetArrayLength());
        Assert.Equal("Fixture Author", merge.GetProperty("authorName").GetString());
        Assert.Single(_calls);
    }

    [Fact]
    public async Task Refs_are_branches_remote_branches_tags_and_checkpoints_from_the_snapshot_without_git()
    {
        await OpenAsync();

        var refs = Result(await RequestAsync("history.refs", new { repoId = "1" })).GetProperty("refs").EnumerateArray()
            .Select(r => (r.GetProperty("name").GetString(), r.GetProperty("kind").GetString())).ToList();

        Assert.Equal(
            [("copilot/checkpoints/session/1", "Checkpoint"), ("feature/åäö", "Branch"), ("main", "Branch"), ("origin/feature/åäö", "RemoteBranch"), ("v1.0", "Tag")],
            refs.Order());
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_tag_points_at_its_commit_not_at_the_tag_object()
    {
        await OpenAsync();
        var head = (await ReadHistoryAsync())[0].GetProperty("id").GetString();

        var tag = Result(await RequestAsync("history.refs", new { repoId = "1" })).GetProperty("refs").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "Tag");

        Assert.Equal(head, tag.GetProperty("targetId").GetString());
    }

    [Fact]
    public async Task Details_list_the_files_against_the_first_parent_with_one_git_call()
    {
        await OpenAsync();

        var details = await DetailsAsync("Merge feature");

        Assert.Equal("Merge feature", details.GetProperty("subject").GetString());
        Assert.Equal(["image.bin", "readme.md"], details.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).Order());
        // One call for the history read that found the commit, one for the details.
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task File_sends_both_sides_of_a_change_with_one_git_call_each()
    {
        await OpenAsync();
        var readme = (await DetailsAsync("Change the readme")).GetProperty("files").EnumerateArray().Single();
        _calls.Clear();

        var before = Result(await RequestAsync("history.file", new { repoId = "1", blobId = readme.GetProperty("oldId").GetString() }));
        var after = Result(await RequestAsync("history.file", new { repoId = "1", blobId = readme.GetProperty("newId").GetString() }));

        Assert.Equal(("Text", "hello\n"), (before.GetProperty("kind").GetString(), before.GetProperty("text").GetString()));
        Assert.Equal(("Text", "hello\nworld\n"), (after.GetProperty("kind").GetString(), after.GetProperty("text").GetString()));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("message").ValueKind);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task A_binary_file_is_answered_with_a_plain_message_instead_of_its_content()
    {
        await OpenAsync();
        var image = (await DetailsAsync("Add an image")).GetProperty("files").EnumerateArray().Single();
        Assert.True(image.GetProperty("isBinary").GetBoolean());

        var file = Result(await RequestAsync("history.file", new { repoId = "1", blobId = image.GetProperty("newId").GetString() }));

        Assert.Equal("Binary", file.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("text").ValueKind);
        Assert.Equal("This is a binary file, so there is no text to show.", file.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("history.read", """{"repoId":"1"}""", "'token' is required")]
    [InlineData("history.read", """{"repoId":"9","token":"t"}""", "No open repo has the id '9'")]
    [InlineData("history.details", """{"repoId":"1","commitId":"HEAD"}""", "'commitId' must be a full commit or file id")]
    [InlineData("history.details", """{"repoId":"1","commitId":"--output=x"}""", "'commitId' must be a full commit or file id")]
    [InlineData("history.file", """{"repoId":"1","blobId":"HEAD:readme.md"}""", "'blobId' must be a full commit or file id")]
    public async Task Bad_params_are_refused_without_starting_git(string method, string parameters, string message)
    {
        await OpenAsync();

        _host.Send($$"""{"jsonrpc":"2.0","id":99,"method":"{{method}}","params":{{parameters}}}""");
        var error = (await _host.NextJsonAsync()).GetProperty("error");

        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.StartsWith(message, error.GetProperty("message").GetString());
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Details_of_a_commit_the_repo_does_not_have_are_a_git_error()
    {
        await OpenAsync();

        var error = (await RequestAsync("history.details", new { repoId = "1", commitId = new string('1', 40) })).GetProperty("error");

        Assert.Equal("GitFailed", error.GetProperty("data").GetProperty("kind").GetString());
    }
}
