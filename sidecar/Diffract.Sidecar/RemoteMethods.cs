using System.Text.Json;
using Diffract.Git;
using Diffract.Git.Changes;

namespace Diffract.Sidecar;

/// <summary>
/// Fetch, pull and push for the branch checked out in one worktree. They write through a changes runner, which allows
/// nothing but <see cref="ChangesPolicy"/>, and answer with what happened and the new status. Cancelling the request
/// stops git.
/// </summary>
internal sealed class RemoteMethods(RepoWorktrees worktrees, GitRunnerOptions runnerOptions)
{
    private readonly ChangesReader _reader = new(runnerOptions);

    public Task<JsonElement> FetchAsync(JsonElement? parameters, CancellationToken ct) =>
        RunAsync(parameters, (sync, t) => sync.FetchAsync(t), ct);

    public Task<JsonElement> PullAsync(JsonElement? parameters, CancellationToken ct) =>
        RunAsync(parameters, (sync, t) => sync.PullAsync(t), ct);

    public Task<JsonElement> PushAsync(JsonElement? parameters, CancellationToken ct) =>
        RunAsync(parameters, (sync, t) => sync.PushAsync(t), ct);

    private async Task<JsonElement> RunAsync(JsonElement? parameters, Func<RemoteSync, CancellationToken, Task<RemoteResult>> run, CancellationToken ct)
    {
        var request = SidecarMethods.Params<ChangesStatusParams>(parameters);
        var worktree = worktrees.Resolve(request.RepoId, request.Worktree);
        var result = await run(new RemoteSync(worktree, runnerOptions), ct).ConfigureAwait(false);
        var status = await _reader.ReadStatusAsync(worktree, ct).ConfigureAwait(false);
        return SidecarMethods.Result(new RemoteSyncResult(result.Outcome, result.Message, status));
    }
}
