using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Diffract.Git;
using Diffract.Leftovers;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <param name="SidecarVersion">The sidecar's own version, for example "0.1.0".</param>
/// <param name="GitVersion">Null when git could not be run.</param>
/// <param name="GitProblem">Why Diffract cannot use this git, in plain English. Null when it can.</param>
internal sealed record AppInfo(string SidecarVersion, string? GitVersion, bool GitSupported, string? GitProblem);

/// <summary>The methods the sidecar answers. Every git call goes through a read-only runner.</summary>
internal sealed class SidecarMethods : IDisposable
{
    /// <summary>Every method <see cref="Dispatch"/> answers. The host itself answers app.shutdown and $/cancelRequest.</summary>
    public static IReadOnlyList<string> Names { get; } = ["app.info", "repo.open", "repo.refresh", "repo.close", "system.openFolder", "system.listTerminals", "system.openTerminal", "git.checkUpdate", "git.update", "git.inUse", "git.openDownloadPage"];

    private readonly RepoSessions _repos;
    private readonly IProcessStarter _processes;
    private readonly TerminalLauncher _terminal;
    private readonly GitUpdateChecker _updates;
    private readonly GitUpdater _updater;

    /// <param name="notify">Sends a notification to the app, such as <c>repo.changed</c>. Tests may leave it out.</param>
    /// <param name="log">Where problems with no request to answer are reported.</param>
    /// <param name="processes">Starts Explorer and terminals. Tests pass a fake.</param>
    /// <param name="machine">Where Windows keeps its programs, for finding terminals and winget. Tests describe their own.</param>
    /// <param name="commands">Runs winget and waits for its answer. Tests pass a fake.</param>
    /// <param name="running">The processes on this machine, for who is using git. Tests describe their own.</param>
    public SidecarMethods(
        GitRunnerOptions runnerOptions,
        DiffractConfig? config = null,
        Func<string, JsonElement, Task>? notify = null,
        TextWriter? log = null,
        IProcessStarter? processes = null,
        MachineLayout? machine = null,
        ICommandRunner? commands = null,
        IProcessTable? running = null)
    {
        RunnerOptions = runnerOptions with { ReadOnly = true };
        notify ??= (_, _) => Task.CompletedTask;
        machine ??= MachineLayout.Current;
        _processes = processes ?? new ProcessStarter();
        _terminal = new TerminalLauncher(_processes, machine);
        commands ??= new CommandRunner(RunnerOptions.TimeProvider);
        _updates = new GitUpdateChecker(commands, machine);
        _updater = new GitUpdater(
            _updates, commands, running ?? new ProcessTable(), machine, RunnerOptions.TimeProvider,
            progress => notify("git.updateProgress", Result(progress)));
        _repos = new RepoSessions(
            RunnerOptions,
            config ?? DiffractConfig.Default,
            RunnerOptions.TimeProvider,
            changed => notify("repo.changed", Result(changed)),
            log ?? TextWriter.Null,
            paused: () => _updater.IsRunning);
    }

    /// <summary>The options every git call uses. Read-only, whatever was passed in.</summary>
    internal GitRunnerOptions RunnerOptions { get; }

    internal RepoSessions Repos => _repos;

    public static string SidecarVersion { get; } =
        typeof(SidecarMethods).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public async Task<JsonElement> Dispatch(string method, JsonElement? parameters, CancellationToken ct)
    {
        try
        {
            switch (method)
            {
                case "app.info":
                    return Result(await AppInfoAsync(ct).ConfigureAwait(false));

                case "repo.open":
                    var open = Params<RepoOpenParams>(parameters);
                    return Result(await _repos.OpenAsync(Required(open.Path, "path"), ct).ConfigureAwait(false));

                case "repo.refresh":
                    var refresh = Params<RepoRefreshParams>(parameters);
                    return Result(await _repos.RefreshAsync(Required(refresh.RepoId, "repoId"), refresh.IncludeUnreachable, ct).ConfigureAwait(false));

                case "repo.close":
                    _repos.Close(Required(Params<RepoCloseParams>(parameters).RepoId, "repoId"));
                    return RpcResponse.NullResult;

                case "system.openFolder":
                    OpenFolder(Required(Params<OpenFolderParams>(parameters).Path, "path"));
                    return RpcResponse.NullResult;

                case "system.listTerminals":
                    return Result(_terminal.List());

                case "system.openTerminal":
                    var terminal = Params<OpenTerminalParams>(parameters);
                    OpenTerminal(ExistingFolder(Required(terminal.Path, "path")), terminal.Terminal);
                    return RpcResponse.NullResult;

                case "git.checkUpdate":
                    return Result(await _updates.CheckAsync(await InstalledGitAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false));

                case "git.update":
                    var version = Required(Params<GitUpdateParams>(parameters).Version, "version");
                    return Result(await _updater.UpdateAsync(await InstalledGitAsync(ct).ConfigureAwait(false), version, ct).ConfigureAwait(false));

                case "git.inUse":
                    return Result(_updater.InUse());

                case "git.openDownloadPage":
                    // A fixed address, so the UI cannot make Explorer open anything else. Explorer hands it to the browser.
                    _processes.Start(Explorer, [GitUpdateChecker.DownloadPage]);
                    return RpcResponse.NullResult;

                default:
                    throw new RpcException(RpcErrorCodes.MethodNotFound, $"Unknown method '{method}'");
            }
        }
        catch (Exception e) when (ToRpcException(e) is { } rpc)
        {
            throw rpc;
        }
    }

    private void OpenTerminal(string folder, TerminalKind? terminal)
    {
        try
        {
            _terminal.Open(folder, terminal);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, e.Message);
        }
    }

    /// <summary>Opens an existing folder in Explorer.</summary>
    private void OpenFolder(string path) => _processes.Start(Explorer, [ExistingFolder(path)]);

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
        // `git version` reads no repo, so any existing folder will do.
        var runner = new GitRunner(AppContext.BaseDirectory, RunnerOptions);
        try
        {
            var version = await GitVersion.DetectAsync(runner, ct);
            return version.IsSupported
                ? new AppInfo(SidecarVersion, version.ToString(), true, null)
                : new AppInfo(SidecarVersion, version.ToString(), false, new GitVersionTooOldException(version).Message);
        }
        catch (Win32Exception)
        {
            return new AppInfo(SidecarVersion, null, false, GitNotFound());
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
    internal RpcException? ToRpcException(Exception e) => e switch
    {
        RpcException => null,
        NotAGitRepositoryException => new(ErrorKind.NotARepo, e.Message),
        GitVersionTooOldException => new(ErrorKind.GitTooOld, e.Message),
        GitTimeoutException => new(ErrorKind.TimedOut, e.Message),
        ReadOnlyViolationException => null,
        GitException => new(ErrorKind.GitFailed, e.Message),
        Win32Exception => new(ErrorKind.GitFailed, GitNotFound()),
        ConfigException => new(ErrorKind.ConfigInvalid, e.Message),
        _ => null,
    };

    private string GitNotFound() =>
        $"Git was not found ({RunnerOptions.GitExecutable}). Install Git for Windows {GitVersion.Minimum} or newer.";

    internal static JsonElement Result<T>(T value) => JsonSerializer.SerializeToElement(value, RpcJson.Type<T>());

    private static T Params<T>(JsonElement? parameters) where T : class
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

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new RpcException(RpcErrorCodes.InvalidParams, $"'{name}' is required") : value;
}
