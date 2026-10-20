using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Diffract.Git;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <param name="SidecarVersion">The sidecar's own version, for example "0.1.0".</param>
/// <param name="GitVersion">Null when git could not be run.</param>
/// <param name="GitProblem">Why Diffract cannot use this git, in plain English. Null when it can.</param>
internal sealed record AppInfo(string SidecarVersion, string? GitVersion, bool GitSupported, string? GitProblem);

/// <summary>The methods the sidecar answers. Every git call goes through a read-only runner.</summary>
internal sealed class SidecarMethods(GitRunnerOptions runnerOptions)
{
    /// <summary>The options every git call uses. Read-only, whatever was passed in.</summary>
    internal GitRunnerOptions RunnerOptions { get; } = runnerOptions with { ReadOnly = true };

    public static string SidecarVersion { get; } =
        typeof(SidecarMethods).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public async Task<JsonElement> Dispatch(string method, JsonElement? parameters, CancellationToken ct) => method switch
    {
        "app.info" => JsonSerializer.SerializeToElement(await AppInfoAsync(ct), RpcJson.AppInfo),
        _ => throw new RpcException(RpcErrorCodes.MethodNotFound, $"Unknown method '{method}'"),
    };

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
            return new AppInfo(SidecarVersion, null, false,
                $"Git was not found ({RunnerOptions.GitExecutable}). Install Git for Windows {GitVersion.Minimum} or newer.");
        }
        catch (Exception e) when (e is GitException or FormatException)
        {
            return new AppInfo(SidecarVersion, null, false, e.Message);
        }
    }
}
