using System.Text.Json;
using Diffract.Git;
using Diffract.Git.Refs;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// The history menu's changes in one worktree: create a branch or tag, switch, and reset the current branch soft or
/// mixed, with undo. They write through a changes runner, which allows nothing but <see cref="ChangesPolicy"/>.
/// <c>ref.checkName</c> only reads. The repo's watcher reports what moved as <c>repo.changed</c>.
/// </summary>
internal sealed class RefMethods(RepoWorktrees worktrees, GitRunnerOptions runnerOptions, CommandOutputs outputs)
{
    /// <summary>Why a new branch or tag name cannot be used, before anything runs. Two reads.</summary>
    public async Task<JsonElement> CheckNameAsync(JsonElement? parameters, CancellationToken ct)
    {
        var check = SidecarMethods.Params<RefCheckNameParams>(parameters);
        var reads = new GitRunner(worktrees.Resolve(check.RepoId, check.Worktree), runnerOptions);
        var kind = check.Kind ?? throw new RpcException(RpcErrorCodes.InvalidParams, "'kind' is required");
        return SidecarMethods.Result(new RefNameCheck(await RefNames.ProblemAsync(reads, kind, check.Name ?? "", ct).ConfigureAwait(false)));
    }

    public Task<JsonElement> CreateBranchAsync(JsonElement? parameters, CancellationToken ct)
    {
        var create = SidecarMethods.Params<BranchCreateParams>(parameters);
        return RunAsync(parameters, create.RepoId, create.Worktree, w => w.CreateBranchAsync(new NewRef(Name(create.Name), Commit(create.Commit)), create.Switch, ct));
    }

    public Task<JsonElement> CreateTagAsync(JsonElement? parameters, CancellationToken ct)
    {
        var create = SidecarMethods.Params<TagCreateParams>(parameters);
        return RunAsync(parameters, create.RepoId, create.Worktree, w => w.CreateTagAsync(new NewRef(Name(create.Name), Commit(create.Commit)), create.Message, ct));
    }

    public Task<JsonElement> SwitchAsync(JsonElement? parameters, CancellationToken ct)
    {
        var target = SidecarMethods.Params<BranchSwitchParams>(parameters);
        return RunAsync(parameters, target.RepoId, target.Worktree, w => w.SwitchAsync(TargetOf(target), ct));
    }

    public async Task<JsonElement> ResetAsync(JsonElement? parameters, CancellationToken ct)
    {
        var reset = SidecarMethods.Params<BranchResetParams>(parameters);
        var writer = new ResetWriter(worktrees.Resolve(reset.RepoId, reset.Worktree), outputs.For(runnerOptions, parameters));
        var mode = reset.Mode ?? throw new RpcException(RpcErrorCodes.InvalidParams, "'mode' is required");
        return SidecarMethods.Result(await Checked(() => writer.ResetAsync(Commit(reset.Commit), mode, ct)).ConfigureAwait(false));
    }

    /// <summary>Takes back the result <c>branch.reset</c> answered with, as it was.</summary>
    public async Task<JsonElement> UndoResetAsync(JsonElement? parameters, CancellationToken ct)
    {
        var undo = SidecarMethods.Params<BranchUndoResetParams>(parameters);
        var writer = new ResetWriter(worktrees.Resolve(undo.RepoId, undo.Worktree), outputs.For(runnerOptions, parameters));
        var reset = undo.Reset ?? throw new RpcException(RpcErrorCodes.InvalidParams, "'reset' is required");
        return SidecarMethods.Result(await Checked(() => writer.UndoAsync(reset, ct)).ConfigureAwait(false));
    }

    private async Task<JsonElement> RunAsync(JsonElement? parameters, string? repoId, string? worktree, Func<RefWriter, Task<RefChange>> run)
    {
        var writer = new RefWriter(worktrees.Resolve(repoId, worktree), outputs.For(runnerOptions, parameters));
        return SidecarMethods.Result(await Checked(() => run(writer)).ConfigureAwait(false));
    }

    /// <summary>Exactly one of a branch, a remote branch or a commit.</summary>
    private static SwitchTarget TargetOf(BranchSwitchParams p) => (p.Branch, p.RemoteBranch, p.Commit) switch
    {
        ({ } branch, null, null) => new SwitchTarget.Branch(branch),
        (null, { } remote, null) => new SwitchTarget.RemoteBranch(remote),
        (null, null, { } commit) => new SwitchTarget.Commit(Commit(commit)),
        _ => throw new RpcException(RpcErrorCodes.InvalidParams, "Give exactly one of 'branch', 'remoteBranch' or 'commit'."),
    };

    private static string Name(string? name) => SidecarMethods.Required(name, "name");

    private static string Commit(string? commit) =>
        ObjectId.IsValid(commit) ? commit! : throw new RpcException(RpcErrorCodes.InvalidParams, "'commit' must be a full commit id");

    /// <summary>A name or id git could take as something else is the caller's mistake.</summary>
    private static async Task<T> Checked<T>(Func<Task<T>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, e.Message);
        }
    }
}
