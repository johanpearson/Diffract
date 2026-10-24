using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Changes;
using Diffract.Git.Files;
using Diffract.Git.History;
using Diffract.Git.Parsing;
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
        ["history.read"] = () => Response(new HistoryReadResult(Commits: 1203, Pages: 3, Tips: [Merge, Side])),
        ["history.update"] = () => Response(new HistoryUpdateResult(
            [new CommitEntry(Side, [Merge], "Claude", "noreply@anthropic.com", Now.AddMinutes(-1), Now.AddMinutes(-1), "Fix the flaky login test")],
            [Parent],
            [Side],
            Reread: false)),
        ["history.page"] = () => Notification("history.page", new HistoryPage("read-1", 0,
        [
            new CommitEntry(Merge, [Parent, Side], "Åsa Öberg", "åsa@exempel.se", Now.AddHours(-3), Now.AddHours(-2), "Merge branch 'claude/räksmörgås-fix'"),
            new CommitEntry(Parent, [], "GitHub Copilot", "198982749+Copilot@users.noreply.github.com", Now.AddDays(-2), Now.AddDays(-2), ""),
        ])),
        ["history.refs"] = () => Response(new HistoryRefs(
            [
                new("main", HistoryRefKind.Branch, Merge, null),
                new("origin/main", HistoryRefKind.RemoteBranch, Parent, null),
                new("v1.0", HistoryRefKind.Tag, Parent, null),
                new("claude/räksmörgås-fix", HistoryRefKind.Branch, Side, "Claude Code"),
                new("copilot/checkpoints/7f3a/1", HistoryRefKind.Checkpoint, Side, "GitHub Copilot"),
                new("stash@{0}", HistoryRefKind.Stash, Side, null),
            ],
            [Side],
            ["copilot checkpoint: "])),
        ["history.details"] = () => Response(new CommitDetails(Merge, [Parent, Side], "Åsa Öberg", "åsa@exempel.se", Now.AddHours(-3),
            "GitHub", "noreply@github.com", Now.AddHours(-2), "Merge branch 'claude/räksmörgås-fix'", "Fixes the cart.\n\nCo-authored-by: Claude <noreply@anthropic.com>",
            [
                new FileChange(FileChangeKind.Renamed, "src/räksmörgås.cs", "src/cart.cs", BlobA, BlobB, 92, 3, 1, IsBinary: false, IsSubmodule: false),
                new FileChange(FileChangeKind.Added, "docs/logo.png", null, null, BlobC, null, null, null, IsBinary: true, IsSubmodule: false),
            ])),
        ["history.file"] = () => Response(HistoryFile.From(new FileContent(FileContentKind.Text, "namespace Shop;\r\n"))),
        ["changes.status"] = () => Response(Changes),
        ["changes.stage"] = () => Response(Changes),
        ["changes.unstage"] = () => Response(Changes),
        ["changes.commit"] = () => Response(new ChangesCommitResult(Side, Changes with { HeadCommit = Side, Files = [] })),
        ["remote.fetch"] = () => Response(new RemoteSyncResult(RemoteOutcome.Done, "Fetched from origin.", Changes)),
        ["remote.pull"] = () => Response(new RemoteSyncResult(RemoteOutcome.Diverged,
            "claude/räksmörgås-fix and origin/claude/räksmörgås-fix have both moved on: 2 commits here and 1 commit there. Pull only fast-forwards for now, so nothing was changed.", Changes)),
        ["remote.push"] = () => Response(new RemoteSyncResult(RemoteOutcome.Rejected,
            "origin/claude/räksmörgås-fix has commits that claude/räksmörgås-fix lacks, so git did not push. Pull first, then push.", Changes)),
        ["changes.file"] = () => Response(ChangesFile.From(new FileDiff(
            new FileContent(FileContentKind.Text, "one\r\ntwo\r\n"),
            new FileContent(FileContentKind.Text, "one\r\nTWO\r\nthree\r\n"),
            [
                new Hunk { Id = "5d41402abc4b2a76b9719d911017c592", OldStart = 2, OldLines = 1, NewStart = 2, NewLines = 1 },
                new Hunk { Id = "7d793037a0760186574b0282f2f435e7", OldStart = 2, OldLines = 0, NewStart = 3, NewLines = 1 },
            ]))),
        ["error.not-a-repo"] = () => Error(new NotAGitRepositoryException(@"C:\Users\johan\Downloads")),
        ["error.git-too-old"] = () => Error(new GitVersionTooOldException(new GitVersion(2, 39, 5))),
        ["error.git-failed"] = () => Error(new GitCommandException(["status", "--porcelain=v2", "-z"], 128, "fatal: unable to read tree\n")),
        ["error.timed-out"] = () => Error(new GitTimeoutException(["fsck", "--unreachable", "--no-reflogs"], TimeSpan.FromSeconds(30))),
        ["error.commit-refused"] = () => Error(new CommitRefusedException(
            "Git did not commit. A hook or commit signing can stop a commit. This is what git said:", "lint failed: src/räksmörgås.cs line 3\n")),
        ["error.remote-failed"] = () => Error(new RemoteFailedException(
            "Git could not fetch from origin. Check the network, and that you can sign in. This is what git said:", "fatal: unable to access 'https://github.com/johan/shop.git/': Could not resolve host: github.com\n")),
        ["error.index-locked"] = () => Error(new IndexLockedException()),
        ["error.changed-since-shown"] = () => Error(new ChangedSinceShownException("src/räksmörgås.cs")),
        ["error.config-invalid"] = () => Error(new ConfigException("staleAfterDays must be at least 1.")),
        ["error.cancelled"] = () => Line(RpcResponse.Failure(Id, RpcErrorCodes.RequestCancelled, "The request was cancelled")),
        ["error.invalid-params"] = () => Line(RpcResponse.Failure(Id, RpcErrorCodes.InvalidParams, "'path' is required")),
        ["enums"] = Enums,
    };

    private static IReadOnlyList<GitInUse> InUse =>
        [new("CodeScene cs-ide tool for windows", "Visual Studio Code", 2), new("Git Bash", null, 3)];

    /// <summary>A worktree with a rename that is also changed, a new file and a conflict.</summary>
    private static WorktreeChanges Changes => new()
    {
        Worktree = AgentWorktree,
        Branch = "claude/räksmörgås-fix",
        HeadCommit = Merge,
        Upstream = "origin/claude/räksmörgås-fix",
        Ahead = 2,
        Behind = 1,
        HeadOnRemote = "origin/claude/räksmörgås-fix",
        Files =
        [
            new ChangedFile
            {
                Path = "src/räksmörgås.cs", OldPath = "src/cart.cs", Kind = ChangedFileKind.Tracked,
                Staged = FileChangeKind.Renamed, Unstaged = FileChangeKind.Modified, HeadId = BlobA, IndexId = BlobB,
            },
            new ChangedFile { Path = "docs/new file.md", Kind = ChangedFileKind.Untracked, Unstaged = FileChangeKind.Added },
            new ChangedFile { Path = "src/merge.cs", Kind = ChangedFileKind.Conflicted },
        ],
    };

    private const string Merge = "9fceb02d0ae598e95dc970b74767f19372d61af8";
    private const string Parent = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const string Side = "e83c5163316f89bfbde7d9ab23ca2e25604af290";
    private const string BlobA = "8ab686eafeb1f44702738c8b0f24f2567c36da6d";
    private const string BlobB = "d670460b4b4aece5915caf5c68d12f560a9fe3e4";
    private const string BlobC = "3b18e512dba79e4c8300dd08aeb37f8e728b8dad";

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
    public void Every_listed_method_has_a_handler_and_every_handler_is_listed()
    {
        Assert.Equal(SidecarMethods.Names.Order(), Methods.HandledMethods.Order());
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
        HostHarness host = null!;
        using var methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(temp) },
            new SidecarDependencies { Notify = (method, parameters) => host.Host.NotifyAsync(method, parameters), Running = new FakeProcessTable() });
        await using var _ = host = new HostHarness(methods.Dispatch);

        host.Send($$$"""{"jsonrpc":"2.0","id":1,"method":"repo.open","params":{"path":{{{JsonSerializer.Serialize(scenario.RepoPath)}}}}}""");
        var opened = Paths(JsonDocument.Parse(await host.NextAsync()).RootElement);
        var scanned = Paths(JsonDocument.Parse(await host.NextAsync()).RootElement);

        Assert.Empty(opened.Except(SamplePaths("repo.open")));
        Assert.Contains("result.scanning", opened);
        Assert.Empty(scanned.Except(SamplePaths("repo.changed")));
        Assert.Contains("params.state.leftovers[].reason", scanned);
        Assert.Contains("params.state.snapshot.worktrees[].lastActivityAt", scanned);
    }

    private static HashSet<string> SamplePaths(string name) =>
        Paths(JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, name + ".json"))).RootElement);

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
        ["historyRefKind"] = Values<HistoryRefKind>(),
        ["fileChangeKind"] = Values<FileChangeKind>(),
        ["fileContentKind"] = Values<FileContentKind>(),
        ["changedFileKind"] = Values<ChangedFileKind>(),
        ["remoteOutcome"] = Values<RemoteOutcome>(),
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

        return RepoState.From(repoId, snapshot, leftovers, new ScanStatus(UnreachableCountedAt: Now.AddMinutes(-10), Scanning: false, Problem: null));
    }
}
