using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Diffract.Git;
using Diffract.Git.History;
using Diffract.Leftovers;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <param name="SidecarVersion">The sidecar's own version, for example "0.1.0".</param>
/// <param name="GitVersion">Null when git could not be run.</param>
/// <param name="GitProblem">Why Diffract cannot use this git, in plain English. Null when it can.</param>
internal sealed record AppInfo(string SidecarVersion, string? GitVersion, bool GitSupported, string? GitProblem);

/// <summary>The parts of the sidecar that tests replace. Anything left out is the real thing.</summary>
internal sealed record SidecarDependencies
{
    public DiffractConfig Config { get; init; } = DiffractConfig.Default;

    /// <summary>Sends a notification to the app, such as <c>repo.changed</c>. Tests may leave it out.</summary>
    public Func<string, JsonElement, Task> Notify { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>Where problems with no request to answer are reported.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;

    /// <summary>Starts Explorer and terminals. Tests pass a fake.</summary>
    public IProcessStarter Processes { get; init; } = new ProcessStarter();

    /// <summary>Where Windows keeps its programs, for finding terminals and winget. Tests describe their own.</summary>
    public MachineLayout Machine { get; init; } = MachineLayout.Current;

    /// <summary>Runs winget and waits for its answer. Null for the real one, on the runner's clock. Tests pass a fake.</summary>
    public ICommandRunner? Commands { get; init; }

    /// <summary>The processes on this machine, for who is using git. Tests describe their own.</summary>
    public IProcessTable Running { get; init; } = new ProcessTable();
}

/// <summary>Answers one method: reads its params and returns its result, or throws <see cref="RpcException"/>.</summary>
internal delegate Task<JsonElement> MethodHandler(JsonElement? parameters, CancellationToken ct);

/// <summary>
/// The methods the sidecar answers. Every git call goes through a read-only runner, apart from the changes the user
/// asks for, such as staging, committing, pushing or creating a branch, which go through a changes runner that allows
/// nothing but <see cref="ChangesPolicy"/>.
/// </summary>
internal sealed class SidecarMethods : IDisposable
{
    /// <summary>Every method <see cref="Dispatch"/> answers. The host itself answers app.shutdown and $/cancelRequest.</summary>
    public static IReadOnlyList<string> Names { get; } = ["app.info", "repo.open", "repo.refresh", "repo.close", "system.openFolder", "system.listTerminals", "system.openTerminal", "git.checkUpdate", "git.update", "git.inUse", "git.openDownloadPage", "history.read", "history.update", "history.refs", "history.details", "history.file", "changes.status", "changes.file", "changes.stage", "changes.unstage", "changes.commit", "remote.fetch", "remote.pull", "remote.push", "ref.checkName", "branch.create", "branch.switch", "tag.create", "branch.reset", "branch.undoReset"];

    /// <summary>
    /// Diffract's own errors and the kind each becomes, most specific first. A null kind, as for a read-only or changes
    /// policy violation, marks a bug: the host logs it as an internal error.
    /// </summary>
    private static readonly (Type Type, ErrorKind? Kind)[] ErrorKinds =
    [
        (typeof(RpcException), null),
        (typeof(NotAGitRepositoryException), ErrorKind.NotARepo),
        (typeof(GitVersionTooOldException), ErrorKind.GitTooOld),
        (typeof(GitTimeoutException), ErrorKind.TimedOut),
        (typeof(ReadOnlyViolationException), null),
        (typeof(ChangesPolicyViolationException), null),
        (typeof(IndexLockedException), ErrorKind.IndexLocked),
        (typeof(ChangedSinceShownException), ErrorKind.ChangedSinceShown),
        (typeof(CommitRefusedException), ErrorKind.CommitRefused),
        (typeof(RemoteFailedException), ErrorKind.RemoteFailed),
        (typeof(ChangeRefusedException), ErrorKind.ChangeRefused),
        (typeof(GitException), ErrorKind.GitFailed),
        (typeof(Win32Exception), ErrorKind.GitFailed),
        (typeof(ConfigException), ErrorKind.ConfigInvalid),
    ];

    private readonly RepoSessions _repos;
    private readonly IProcessStarter _processes;
    private readonly TerminalLauncher _terminal;
    private readonly GitUpdateChecker _updates;
    private readonly GitUpdater _updater;
    private readonly HistoryReader _history;
    private readonly HistoryUpdater _historyUpdates;
    private readonly ChangesMethods _changes;
    private readonly RemoteMethods _remote;
    private readonly RefMethods _refs;
    private readonly DiffractConfig _config;
    private readonly Func<string, JsonElement, Task> _notify;
    private readonly Dictionary<string, MethodHandler> _handlers;

    public SidecarMethods(GitRunnerOptions runnerOptions, SidecarDependencies? dependencies = null)
    {
        var d = dependencies ?? new SidecarDependencies();
        RunnerOptions = runnerOptions with { ReadOnly = true };
        _notify = d.Notify;
        _history = new HistoryReader(RunnerOptions);
        _historyUpdates = new HistoryUpdater(RunnerOptions);
        _config = d.Config;
        _processes = d.Processes;
        _terminal = new TerminalLauncher(_processes, d.Machine);
        var commands = d.Commands ?? new CommandRunner(RunnerOptions.TimeProvider);
        _updates = new GitUpdateChecker(commands, d.Machine);
        _updater = new GitUpdater(_updates, commands, d.Running, d.Machine);
        _repos = new RepoSessions(
            RunnerOptions,
            d.Config,
            RunnerOptions.TimeProvider,
            changed => _notify("repo.changed", Result(changed)),
            d.Log,
            paused: () => _updater.IsRunning);
        var worktrees = new RepoWorktrees(_repos);
        var outputs = new CommandOutputs(_notify);
        _changes = new ChangesMethods(worktrees, RunnerOptions, outputs);
        _remote = new RemoteMethods(worktrees, RunnerOptions, outputs);
        _refs = new RefMethods(worktrees, RunnerOptions, outputs);
        _handlers = new Dictionary<string, MethodHandler>
        {
            ["app.info"] = async (_, ct) => Result(await AppInfoAsync(ct).ConfigureAwait(false)),
            ["repo.open"] = OpenRepoAsync,
            ["repo.refresh"] = RefreshRepoAsync,
            ["repo.close"] = CloseRepo,
            ["history.read"] = ReadHistoryAsync,
            ["history.update"] = UpdateHistoryAsync,
            ["history.refs"] = ReadHistoryRefs,
            ["history.details"] = HistoryDetailsAsync,
            ["history.file"] = HistoryFileAsync,
            ["changes.status"] = _changes.StatusAsync,
            ["changes.file"] = _changes.FileAsync,
            ["changes.stage"] = _changes.StageAsync,
            ["changes.unstage"] = _changes.UnstageAsync,
            ["changes.commit"] = _changes.CommitAsync,
            ["remote.fetch"] = _remote.FetchAsync,
            ["remote.pull"] = _remote.PullAsync,
            ["remote.push"] = _remote.PushAsync,
            ["ref.checkName"] = _refs.CheckNameAsync,
            ["branch.create"] = _refs.CreateBranchAsync,
            ["branch.switch"] = _refs.SwitchAsync,
            ["tag.create"] = _refs.CreateTagAsync,
            ["branch.reset"] = _refs.ResetAsync,
            ["branch.undoReset"] = _refs.UndoResetAsync,
            ["system.openFolder"] = OpenFolder,
            ["system.listTerminals"] = (_, _) => Task.FromResult(Result(_terminal.List())),
            ["system.openTerminal"] = OpenTerminal,
            ["git.checkUpdate"] = CheckGitUpdateAsync,
            ["git.update"] = UpdateGitAsync,
            ["git.inUse"] = (_, _) => Task.FromResult(Result(_updater.InUse())),
            ["git.openDownloadPage"] = OpenDownloadPage,
        };
    }

    /// <summary>The options every git call uses. Read-only, whatever was passed in.</summary>
    internal GitRunnerOptions RunnerOptions { get; }

    internal RepoSessions Repos => _repos;

    /// <summary>The methods there is a handler for. A test checks they are exactly <see cref="Names"/>.</summary>
    internal IEnumerable<string> HandledMethods => _handlers.Keys;

    public static string SidecarVersion { get; } =
        typeof(SidecarMethods).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public async Task<JsonElement> Dispatch(string method, JsonElement? parameters, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(method, out var handle))
            throw new RpcException(RpcErrorCodes.MethodNotFound, $"Unknown method '{method}'");

        try
        {
            return await handle(parameters, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (ToRpcException(e) is { } rpc)
        {
            throw rpc;
        }
    }

    private async Task<JsonElement> OpenRepoAsync(JsonElement? parameters, CancellationToken ct)
    {
        var open = Params<RepoOpenParams>(parameters);
        return Result(await _repos.OpenAsync(Required(open.Path, "path"), ct).ConfigureAwait(false));
    }

    private async Task<JsonElement> RefreshRepoAsync(JsonElement? parameters, CancellationToken ct)
    {
        var refresh = Params<RepoRefreshParams>(parameters);
        return Result(await _repos.RefreshAsync(Required(refresh.RepoId, "repoId"), refresh.IncludeUnreachable, ct).ConfigureAwait(false));
    }

    private Task<JsonElement> CloseRepo(JsonElement? parameters, CancellationToken ct)
    {
        _repos.Close(Required(Params<RepoCloseParams>(parameters).RepoId, "repoId"));
        return Task.FromResult(RpcResponse.NullResult);
    }

    /// <summary>
    /// Streams the whole history as <c>history.page</c> notifications, then answers with the totals and the tips it
    /// was read from. One git call, whatever the size. Cancelling the request stops git.
    /// </summary>
    private async Task<JsonElement> ReadHistoryAsync(JsonElement? parameters, CancellationToken ct)
    {
        var read = Params<HistoryReadParams>(parameters);
        var snapshot = _repos.SnapshotOf(Required(read.RepoId, "repoId"));
        var token = Required(read.Token, "token");
        var tips = new HistoryScope(snapshot, _config).Tips(new HistoryShow(read.Checkpoints, read.Stashes));
        var pages = 0;
        var commits = await _history.ReadAsync(snapshot.RepoRoot, tips,
            page => _notify("history.page", Result(new HistoryPage(token, pages++, page))), ct).ConfigureAwait(false);
        return Result(new HistoryReadResult(commits, pages, tips));
    }

    /// <summary>What changed since the UI's history was read from its tips: none, one or two git calls.</summary>
    private async Task<JsonElement> UpdateHistoryAsync(JsonElement? parameters, CancellationToken ct)
    {
        var update = Params<HistoryUpdateParams>(parameters);
        var snapshot = _repos.SnapshotOf(Required(update.RepoId, "repoId"));
        var oldTips = update.Tips is { } given && given.All(ObjectId.IsValid)
            ? given
            : throw new RpcException(RpcErrorCodes.InvalidParams, "'tips' must be a list of full commit ids");
        var tips = new HistoryScope(snapshot, _config).Tips(new HistoryShow(update.Checkpoints, update.Stashes));
        var change = await _historyUpdates.UpdateAsync(snapshot.RepoRoot, oldTips, tips, ct).ConfigureAwait(false);
        return Result(new HistoryUpdateResult(change.Added, change.Removed, tips, change.TooMany));
    }

    private Task<JsonElement> ReadHistoryRefs(JsonElement? parameters, CancellationToken ct) =>
        Task.FromResult(Result(new HistoryScope(_repos.SnapshotOf(Required(Params<HistoryRefsParams>(parameters).RepoId, "repoId")), _config).Refs()));

    private async Task<JsonElement> HistoryDetailsAsync(JsonElement? parameters, CancellationToken ct)
    {
        var details = Params<HistoryDetailsParams>(parameters);
        var repoRoot = _repos.SnapshotOf(Required(details.RepoId, "repoId")).RepoRoot;
        return Result(await _history.ReadDetailsAsync(repoRoot, RequiredObjectId(details.CommitId, "commitId"), ct).ConfigureAwait(false));
    }

    private async Task<JsonElement> HistoryFileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var file = Params<HistoryFileParams>(parameters);
        var repoRoot = _repos.SnapshotOf(Required(file.RepoId, "repoId")).RepoRoot;
        return Result(HistoryFile.From(await _history.ReadFileAsync(repoRoot, RequiredObjectId(file.BlobId, "blobId"), ct).ConfigureAwait(false)));
    }

    /// <summary>Opens an existing folder in Explorer.</summary>
    private Task<JsonElement> OpenFolder(JsonElement? parameters, CancellationToken ct)
    {
        _processes.Start(Explorer, [ExistingFolder(Required(Params<OpenFolderParams>(parameters).Path, "path"))]);
        return Task.FromResult(RpcResponse.NullResult);
    }

    private Task<JsonElement> OpenTerminal(JsonElement? parameters, CancellationToken ct)
    {
        var terminal = Params<OpenTerminalParams>(parameters);
        var folder = ExistingFolder(Required(terminal.Path, "path"));
        try
        {
            _terminal.Open(folder, terminal.Terminal);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, e.Message);
        }
        return Task.FromResult(RpcResponse.NullResult);
    }

    private async Task<JsonElement> CheckGitUpdateAsync(JsonElement? parameters, CancellationToken ct) =>
        Result(await _updates.CheckAsync(await InstalledGitAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false));

    private async Task<JsonElement> UpdateGitAsync(JsonElement? parameters, CancellationToken ct)
    {
        var version = Required(Params<GitUpdateParams>(parameters).Version, "version");
        return Result(await _updater.UpdateAsync(await InstalledGitAsync(ct).ConfigureAwait(false), version, ct).ConfigureAwait(false));
    }

    /// <summary>A fixed address, so the UI cannot make Explorer open anything else. Explorer hands it to the browser.</summary>
    private Task<JsonElement> OpenDownloadPage(JsonElement? parameters, CancellationToken ct)
    {
        _processes.Start(Explorer, [GitUpdateChecker.DownloadPage]);
        return Task.FromResult(RpcResponse.NullResult);
    }

    /// <summary>The full path, so a program called explorer.exe elsewhere on PATH is never the one started.</summary>
    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// The full path of an existing folder, without a trailing separator. Anything else is refused, so the UI can
    /// only ever point Explorer or a terminal at a folder, never at a program or a file.
    /// </summary>
    private static string ExistingFolder(string path)
    {
        var full = Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        return full is not null && Directory.Exists(full)
            ? full
            : throw new RpcException(RpcErrorCodes.InvalidParams, $"The folder does not exist: {path}");
    }

    /// <summary>Stops watching every open repo.</summary>
    public void Dispose() => _repos.Dispose();

    public async Task<AppInfo> AppInfoAsync(CancellationToken ct)
    {
        // Asked at start and after an update, which may have moved git, so git is looked for again first.
        RunnerOptions.Locator?.FindAgain();
        // `git version` reads no repo, so any existing folder will do.
        var runner = new GitRunner(AppContext.BaseDirectory, RunnerOptions);
        try
        {
            var version = await GitVersion.DetectAsync(runner, ct);
            return version.IsSupported
                ? new AppInfo(SidecarVersion, version.ToString(), true, null)
                : new AppInfo(SidecarVersion, version.ToString(), false, new GitVersionTooOldException(version).Message);
        }
        catch (Win32Exception e)
        {
            return new AppInfo(SidecarVersion, null, false, GitNotStarted(e));
        }
        catch (Exception e) when (e is GitException or FormatException)
        {
            return new AppInfo(SidecarVersion, null, false, e.Message);
        }
    }

    /// <summary>The git Diffract uses, or null when it cannot be run or its answer cannot be read.</summary>
    private async Task<GitVersion?> InstalledGitAsync(CancellationToken ct)
    {
        try
        {
            return await GitVersion.DetectAsync(new GitRunner(AppContext.BaseDirectory, RunnerOptions), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or GitException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Diffract's own errors become JSON-RPC errors with a kind, and the plain-English message the spike prints.
    /// Anything else, including a read-only violation, is a bug: null, so the host logs it as an internal error.
    /// </summary>
    internal RpcException? ToRpcException(Exception e) =>
        ErrorKinds.FirstOrDefault(k => k.Type.IsInstanceOfType(e)).Kind is { } kind
            ? new RpcException(kind, e is Win32Exception w ? GitNotStarted(w) : e.Message)
            : null;

    /// <summary>Why git could not be started. Only a missing file is "not found": git may also be blocked for a moment.</summary>
    private string GitNotStarted(Win32Exception e) => e.NativeErrorCode is FileNotFound or PathNotFound
        ? $"Git was not found ({RunnerOptions.Installation.Executable}). Install Git for Windows {GitVersion.Minimum} or newer."
        : $"Git could not be started ({RunnerOptions.Installation.Executable}): {e.Message}";

    private const int FileNotFound = 2;
    private const int PathNotFound = 3;

    internal static JsonElement Result<T>(T value) => JsonSerializer.SerializeToElement(value, RpcJson.Type<T>());

    internal static T Params<T>(JsonElement? parameters) where T : class
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p)
            throw new RpcException(RpcErrorCodes.InvalidParams, "Params must be an object");

        try
        {
            return p.Deserialize(RpcJson.Type<T>())!;
        }
        catch (JsonException e)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Params are not valid: {e.Message}");
        }
    }

    private static string RequiredObjectId(string? value, string name) =>
        ObjectId.IsValid(value) ? value! : throw new RpcException(RpcErrorCodes.InvalidParams, $"'{name}' must be a full commit or file id");

    internal static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new RpcException(RpcErrorCodes.InvalidParams, $"'{name}' is required") : value;
}
