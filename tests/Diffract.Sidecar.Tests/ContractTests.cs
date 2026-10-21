using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// Writes every response and notification shape to <c>Contract/*.json</c>, from the same serializer the host uses.
/// The UI's tests read the same files, so the C# and TypeScript sides cannot drift apart unnoticed.
/// A sample that no longer matches is rewritten and the test fails: check the change, then commit the file.
/// </summary>
public class ContractTests
{
    private static readonly string Directory = Path.Combine(RepoRoot.Find(), "tests", "Diffract.Sidecar.Tests", "Contract");

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonElement Id = JsonSerializer.SerializeToElement(1);

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 30, 0, TimeSpan.FromHours(2));

    private static readonly SidecarMethods Methods = new(new GitRunnerOptions());

    /// <summary>Sample name (the file name without .json) and the exact line the sidecar writes for it.</summary>
    private static readonly Dictionary<string, Func<string>> Samples = new()
    {
        ["app.info"] = () => Response(new AppInfo("0.1.0", "2.47.1", true, null)),
        ["repo.open"] = () => Response(State("1")),
        ["repo.refresh"] = () => Response(State("1")),
        ["repo.close"] = () => Response(RpcResponse.NullResult),
        ["system.openFolder"] = () => Response(RpcResponse.NullResult),
        ["system.listTerminals"] = () => Response<IReadOnlyList<TerminalInfo>>(
            [new(TerminalKind.WindowsTerminal, "Windows Terminal"), new(TerminalKind.WindowsPowerShell, "Windows PowerShell"), new(TerminalKind.GitBash, "Git Bash")]),
        ["system.openTerminal"] = () => Response(RpcResponse.NullResult),
        ["git.checkUpdate"] = () => Response(new GitUpdateCheck(GitUpdateStatus.UpdateAvailable, "2.47.1", "2.55.0.5", null, GitUpdateChecker.DownloadPage)),
        ["git.update"] = () => Response(GitUpdater.Outcome(unchecked((int)0x8A150006), "2.55.0.5", () => InUse)),
        ["git.inUse"] = () => Response(InUse),
        ["git.openDownloadPage"] = () => Response(RpcResponse.NullResult),
        ["app.shutdown"] = () => Response(RpcResponse.NullResult),
        ["repo.changed"] = () => Notification("repo.changed", new RepoChanged(
            [new RepoChange(ChangeKind.WorktreeFiles, AgentWorktree), new RepoChange(ChangeKind.Refs, null)],
            State("1"))),
        ["error.not-a-repo"] = () => Error(new NotAGitRepositoryException(@"C:\Users\johan\Downloads")),
        ["error.git-too-old"] = () => Error(new GitVersionTooOldException(new GitVersion(2, 39, 5))),
        ["error.git-failed"] = () => Error(new GitCommandException(["status", "--porcelain=v2", "-z"], 128, "fatal: unable to read tree\n")),
        ["error.timed-out"] = () => Error(new GitTimeoutException(["fsck", "--unreachable", "--no-reflogs"], TimeSpan.FromSeconds(30))),
        ["error.config-invalid"] = () => Error(new ConfigException("staleAfterDays must be at least 1.")),
        ["error.cancelled"] = () => Line(RpcResponse.Failure(Id, RpcErrorCodes.RequestCancelled, "The request was cancelled")),
        ["error.invalid-params"] = () => Line(RpcResponse.Failure(Id, RpcErrorCodes.InvalidParams, "'path' is required")),
        ["enums"] = Enums,
    };

    private static IReadOnlyList<GitInUse> InUse =>
        [new("CodeScene cs-ide tool for windows", "Visual Studio Code", 2), new("Git Bash", null, 3)];

    private const string MainWorktree = @"C:\src\shop";
    private const string AgentWorktree = @"C:\src\shop\.claude\worktrees\räksmörgås fix";

    public static TheoryData<string> SampleNames => [.. Samples.Keys];

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Sample_matches_what_the_sidecar_writes(string name)
    {
        var expected = JsonNode.Parse(Samples[name]())!.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(Directory, name + ".json");
        var committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;

        if (committed != expected)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, expected);
            Assert.Fail($"Contract/{name}.json was out of date and has been rewritten. Check the change, run the UI tests and commit the file.");
        }
    }

    [Fact]
    public void Every_sample_file_is_still_written_by_a_test()
    {
        var files = System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "*.json").Select(Path.GetFileNameWithoutExtension)
            : [];

        Assert.Empty(files.Except(Samples.Keys));
    }

    [Fact]
    public void Every_method_has_a_response_sample()
    {
        Assert.All(SidecarMethods.Names.Append(RpcHost.ShutdownMethod), method => Assert.Contains(method, Samples.Keys));
    }

    [Fact]
    public void Every_error_kind_has_a_sample()
    {
        var kinds = Samples.Where(s => s.Key.StartsWith("error.", StringComparison.Ordinal))
            .Select(s => JsonDocument.Parse(s.Value()).RootElement.GetProperty("error"))
            .Where(e => e.TryGetProperty("data", out _))
            .Select(e => Enum.Parse<ErrorKind>(e.GetProperty("data").GetProperty("kind").GetString()!));

        Assert.Equal(Enum.GetValues<ErrorKind>().Order(), kinds.Order());
    }

    [Fact]
    public async Task A_real_repo_open_writes_nothing_the_sample_lacks()
    {
        using var temp = new TempDirectory();
        var scenario = await new FixtureRepoBuilder(temp).StaleAgentWorktreeAsync();
        using var methods = new SidecarMethods(new GitRunnerOptions { Environment = IsolatedGit.Environment(temp) });
        await using var host = new HostHarness(methods.Dispatch);

        host.Send($$$"""{"jsonrpc":"2.0","id":1,"method":"repo.open","params":{"path":{{{JsonSerializer.Serialize(scenario.RepoPath)}}}}}""");
        var real = Paths(JsonDocument.Parse(await host.NextAsync()).RootElement);

        var sample = Paths(JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "repo.open.json"))).RootElement);
        Assert.Empty(real.Except(sample));
        Assert.Contains("result.leftovers[].reason", real);
        Assert.Contains("result.snapshot.worktrees[].lastActivityAt", real);
    }

    /// <summary>Every property path in a JSON document, with arrays written as [].</summary>
    private static HashSet<string> Paths(JsonElement element, string prefix = "")
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    paths.Add(path);
                    paths.UnionWith(Paths(property.Value, path));
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    paths.UnionWith(Paths(item, prefix + "[]"));
                break;
        }
        return paths;
    }

    private static string Response<T>(T result) => Line(RpcResponse.Success(Id, SidecarMethods.Result(result)));

    private static string Response(JsonElement result) => Line(RpcResponse.Success(Id, result));

    private static string Error(Exception e)
    {
        var rpc = Methods.ToRpcException(e) ?? throw new InvalidOperationException($"{e.GetType().Name} is not a Diffract error");
        return Line(RpcResponse.Failure(Id, rpc.Code, rpc.Message, rpc.Kind));
    }

    private static string Notification<T>(string method, T parameters) =>
        JsonSerializer.Serialize(new RpcNotification(method, SidecarMethods.Result(parameters)), RpcJson.Type<RpcNotification>());

    private static string Line(RpcResponse response) => JsonSerializer.Serialize(response, RpcJson.Type<RpcResponse>());

    /// <summary>Every value of every enum in the protocol, as the sidecar writes it.</summary>
    private static string Enums() => new JsonObject
    {
        ["verdict"] = Values<Verdict>(),
        ["leftoverKind"] = Values<LeftoverKind>(),
        ["errorKind"] = Values<ErrorKind>(),
        ["changeKind"] = Values<ChangeKind>(),
        ["terminalKind"] = Values<TerminalKind>(),
        ["gitUpdateStatus"] = Values<GitUpdateStatus>(),
        ["gitUpdateOutcome"] = Values<GitUpdateOutcome>(),
    }.ToJsonString();

    private static JsonArray Values<T>() where T : struct, Enum =>
        [.. Enum.GetValues<T>().Select(v => (JsonNode)JsonSerializer.SerializeToNode(v, RpcJson.Type<T>())!)];

    /// <summary>A repo with a main worktree, a locked agent worktree with an awkward path, and one leftover of each verdict.</summary>
    private static RepoState State(string repoId)
    {
        var snapshot = new RepoSnapshot(
            RepoRoot: MainWorktree,
            MainBranch: "main",
            GitVersion: new GitVersion(2, 47, 1),
            Worktrees:
            [
                new WorktreeInfo(MainWorktree, IsMain: true, Head: "4b825dc642cb6eb9a060e54bf8d69288fbee4904", Branch: "main",
                    IsBare: false, IsDetached: false, IsLocked: false, LockReason: null, IsPrunable: false, PrunableReason: null,
                    DirtyFileCount: 0, AheadOfMain: 0, BehindMain: 0, LastCommitAt: Now.AddDays(-1), LastActivityAt: Now.AddHours(-2)),
                new WorktreeInfo(AgentWorktree, IsMain: false, Head: "9fceb02d0ae598e95dc970b74767f19372d61af8", Branch: "claude/räksmörgås-fix",
                    IsBare: false, IsDetached: false, IsLocked: true, LockReason: "claude agent agent-a1b2c3 (pid 4242)", IsPrunable: false, PrunableReason: null,
                    DirtyFileCount: 3, AheadOfMain: 2, BehindMain: null, LastCommitAt: null, LastActivityAt: Now.AddMinutes(-4)),
            ],
            Branches: [],
            Tags: [],
            Stashes: [],
            OtherRefs: [],
            UnreachableCommits: [],
            LeftoverFolders: [new LeftoverFolderInfo(@"C:\src\shop\.claude\worktrees\old-task", HoldsNoFiles: true, WasWorktree: false)],
            TakenAt: Now);

        IReadOnlyList<Leftover> leftovers =
        [
            new(LeftoverKind.LeftoverFolder, "old-task", @"C:\src\shop\.claude\worktrees\old-task", "Claude Code", Verdict.SafeToRemove, null,
                "Empty folder left in a worktree container. Removing it loses nothing."),
            new(LeftoverKind.LocalBranch, "copilot/fix-login", null, "GitHub Copilot", Verdict.NeedsReview, 2,
                "Not merged into main and untouched for 21 days."),
            new(LeftoverKind.Worktree, "räksmörgås fix", AgentWorktree, null, Verdict.InUse, 2,
                "Locked: claude agent agent-a1b2c3 (pid 4242)."),
        ];

        return RepoState.From(repoId, snapshot, leftovers, unreachableCountedAt: Now.AddMinutes(-10));
    }
}
