using System.Text.Json;
using Diffract.Git;
using Diffract.Git.Changes;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// Saving and applying stashes in one worktree, through a changes runner that allows nothing but
/// <see cref="ChangesPolicy"/>. Both answer with the worktree's status afterwards. Dropping a stash is a cleanup, with a
/// backup tag and undo: <c>cleanup.planStash</c>, then <c>cleanup.apply</c>.
/// </summary>
internal sealed class StashMethods(RepoWorktrees worktrees, GitRunnerOptions runnerOptions, CommandOutputs outputs)
{
    private readonly ChangesReader _reader = new(runnerOptions);

    public async Task<JsonElement> PushAsync(JsonElement? parameters, CancellationToken ct)
    {
        var push = SidecarMethods.Params<StashPushParams>(parameters);
        var worktree = worktrees.Resolve(push.RepoId, push.Worktree);
        var saved = await Writer(worktree, parameters).PushAsync(new StashRequest(push.Message, push.IncludeUntracked), ct).ConfigureAwait(false);
        var status = await _reader.ReadStatusAsync(worktree, ct).ConfigureAwait(false);
        return SidecarMethods.Result(new StashPushResult(saved.CommitId, saved.Message, status));
    }

    public async Task<JsonElement> ApplyAsync(JsonElement? parameters, CancellationToken ct)
    {
        var apply = SidecarMethods.Params<StashApplyParams>(parameters);
        var worktree = worktrees.Resolve(apply.RepoId, apply.Worktree);
        var commit = ObjectId.IsValid(apply.Commit) ? apply.Commit! : throw new RpcException(RpcErrorCodes.InvalidParams, "'commit' must be a full commit id");
        var applied = await Writer(worktree, parameters).ApplyAsync(commit, ct).ConfigureAwait(false);
        var status = await _reader.ReadStatusAsync(worktree, ct).ConfigureAwait(false);
        return SidecarMethods.Result(new StashApplyResult(applied.Conflicts, applied.Message, status));
    }

    private StashWriter Writer(string worktree, JsonElement? parameters) => new(worktree, outputs.For(runnerOptions, parameters));
}
