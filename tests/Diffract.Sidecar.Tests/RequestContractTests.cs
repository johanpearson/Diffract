using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffract.Git.Refs;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// Writes the params of every method that takes any to <c>Contract/requests/*.json</c>, from the sidecar's own params
/// types and serializer. The UI's tests check each one against the TypeScript params it sends, so a field the two
/// sides name or type differently fails a test instead of arriving as null. Every field is set, so none goes unchecked;
/// a sample is about names and types, so a "one of these" params type has all its choices set at once.
/// </summary>
public class RequestContractTests
{
    private static readonly string Directory = Path.Combine(RepoRoot.Find(), "tests", "Diffract.Sidecar.Tests", "Contract", "requests");

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The methods that take no params.</summary>
    private static readonly string[] NoParams = ["app.info", "app.shutdown", "system.listTerminals", "git.checkUpdate", "git.inUse", "git.openDownloadPage"];

    private const string Repo = "1";
    private const string Worktree = @"C:\src\shop\.claude\worktrees\räksmörgås fix";
    private const string Commit = "e83c5163316f89bfbde7d9ab23ca2e25604af290";
    private const string Blob = "8ab686eafeb1f44702738c8b0f24f2567c36da6d";

    private static readonly ChangesStatusParams InWorktree = new(Repo, Worktree);

    private static readonly ChangesStageParams Stage = new()
    {
        RepoId = Repo,
        Worktree = Worktree,
        Paths = ["src/räksmörgås.cs", "src/cart.cs"],
        Hunk = new ChangesHunkParams { Path = "src/räksmörgås.cs", OldPath = "src/cart.cs", Id = "5d41402abc4b2a76b9719d911017c592" },
    };

    private static readonly Dictionary<string, Func<JsonNode>> Samples = new()
    {
        ["repo.open"] = () => Node(new RepoOpenParams(@"C:\src\shop")),
        ["repo.refresh"] = () => Node(new RepoRefreshParams(Repo, true)),
        ["repo.close"] = () => Node(new RepoCloseParams(Repo)),
        ["system.openFolder"] = () => Node(new OpenFolderParams(Worktree)),
        ["system.openTerminal"] = () => Node(new OpenTerminalParams(Worktree, TerminalKind.GitBash)),
        ["git.update"] = () => Node(new GitUpdateParams("2.55.0.5")),
        ["history.read"] = () => Node(new HistoryReadParams(Repo, "read-1", true, false)),
        ["history.update"] = () => Node(new HistoryUpdateParams(Repo, [Commit], false, true)),
        ["history.refs"] = () => Node(new HistoryRefsParams(Repo)),
        ["history.details"] = () => Node(new HistoryDetailsParams(Repo, Commit)),
        ["history.file"] = () => Node(new HistoryFileParams(Repo, Blob)),
        ["changes.status"] = () => Node(InWorktree),
        ["changes.file"] = () => Node(new ChangesFileParams
        {
            RepoId = Repo, Worktree = Worktree, Path = "src/räksmörgås.cs", OldPath = "src/cart.cs", Staged = true, HeadId = Blob, IndexId = Blob,
        }),
        ["changes.stage"] = () => Node(Stage),
        ["changes.unstage"] = () => Node(Stage),
        ["changes.commit"] = () => Node(new ChangesCommitParams { RepoId = Repo, Worktree = Worktree, Message = "Fix the cart\n\nWith åäö.", Amend = true }),
        ["remote.fetch"] = () => Node(InWorktree),
        ["remote.pull"] = () => Node(InWorktree),
        ["remote.push"] = () => Node(InWorktree),
        ["ref.checkName"] = () => Node(new RefCheckNameParams(Repo, Worktree, RefNameKind.Branch, "fix/räksmörgås")),
        ["branch.create"] = () => Node(new BranchCreateParams { RepoId = Repo, Worktree = Worktree, Commit = Commit, Name = "fix/räksmörgås", Switch = true }),
        ["branch.switch"] = () => Node(new BranchSwitchParams { RepoId = Repo, Worktree = Worktree, Branch = "main", RemoteBranch = "origin/topic", Commit = Commit }),
        ["tag.create"] = () => Node(new TagCreateParams { RepoId = Repo, Worktree = Worktree, Commit = Commit, Name = "v1.0", Message = "Första släppet" }),
        ["branch.reset"] = () => Node(new BranchResetParams(Repo, Worktree, Commit, ResetMode.Mixed)),
        ["branch.undoReset"] = () => Node(new BranchUndoResetParams(Repo, Worktree, new ResetResult
        {
            Branch = "claude/räksmörgås-fix", OldCommit = "9fceb02d0ae598e95dc970b74767f19372d61af8", NewCommit = Commit, Mode = ResetMode.Soft,
        })),
    };

    public static TheoryData<string> SampleNames => [.. Samples.Keys];

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Sample_matches_the_sidecars_params_type(string method)
    {
        var expected = Samples[method]().ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(Directory, method + ".json");
        var committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;

        if (committed != expected)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, expected);
            Assert.Fail($"Contract/requests/{method}.json was out of date and has been rewritten. Check the change, run the UI tests and commit the file.");
        }
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Every_field_is_set(string method) => Assert.Empty(NullPaths(Samples[method](), "$"));

    [Fact]
    public void Every_method_with_params_has_a_sample_and_every_sample_a_method()
    {
        Assert.Equal(SidecarMethods.Names.Except(NoParams).Order(), Samples.Keys.Order());
        var files = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.json").Select(Path.GetFileNameWithoutExtension) : [];
        Assert.Empty(files.Except(Samples.Keys));
    }

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, RpcJson.Type<T>())!;

    /// <summary>Where the sample has a null, which would leave that field's name and type unchecked.</summary>
    private static IEnumerable<string> NullPaths(JsonNode? node, string path) => node switch
    {
        null => [path],
        JsonObject o => o.SelectMany(p => NullPaths(p.Value, $"{path}.{p.Key}")),
        JsonArray a => a.SelectMany((item, i) => NullPaths(item, $"{path}[{i}]")),
        _ => [],
    };
}
