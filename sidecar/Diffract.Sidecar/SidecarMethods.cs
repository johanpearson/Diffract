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
internal sealed class SidecarMethods
{
    /// <summary>Every method <see cref="Dispatch"/> answers. The host itself answers app.shutdown and $/cancelRequest.</summary>
    public static IReadOnlyList<string> Names { get; } = ["app.info", "repo.open", "repo.refresh", "repo.close"];

    private readonly RepoSessions _repos;

    public SidecarMethods(GitRunnerOptions runnerOptions, DiffractConfig? config = null)
    {
        RunnerOptions = runnerOptions with { ReadOnly = true };
        _repos = new RepoSessions(RunnerOptions, config ?? DiffractConfig.Default, RunnerOptions.TimeProvider);
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

                default:
                    throw new RpcException(RpcErrorCodes.MethodNotFound, $"Unknown method '{method}'");
            }
        }
        catch (Exception e) when (ToRpcException(e) is { } rpc)
        {
            throw rpc;
        }
    }

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
