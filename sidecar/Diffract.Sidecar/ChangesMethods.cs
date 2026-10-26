using System.Text.Json;
using Diffract.Git;
using Diffract.Git.Changes;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// The Changes view's methods for one worktree. <c>changes.status</c> and <c>changes.file</c> only read.
/// <c>changes.stage</c>, <c>changes.unstage</c> and <c>changes.commit</c> write through a changes runner, which allows nothing but
/// <see cref="ChangesPolicy"/>. The worktree must be one in the repo's snapshot, so git only ever runs where the repo lives.
/// </summary>
internal sealed class ChangesMethods(RepoWorktrees worktrees, GitRunnerOptions runnerOptions, CommandOutputs outputs)
{
    private readonly ChangesReader _reader = new(runnerOptions);

    /// <summary>The worktree's branch and changed files. One git call.</summary>
    public async Task<JsonElement> StatusAsync(JsonElement? parameters, CancellationToken ct)
    {
        var status = SidecarMethods.Params<ChangesStatusParams>(parameters);
        var worktree = worktrees.Resolve(status.RepoId, status.Worktree);
        return SidecarMethods.Result(await _reader.ReadStatusAsync(worktree, ct).ConfigureAwait(false));
    }

    /// <summary>The two sides of one file's diff, staged or not, and its hunks. At most three git calls, run side by side.</summary>
    public async Task<JsonElement> FileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var file = SidecarMethods.Params<ChangesFileParams>(parameters);
        var worktree = worktrees.Resolve(file.RepoId, file.Worktree);
        var request = new FileDiffRequest
        {
            Path = SidecarMethods.Required(file.Path, "path"),
            OldPath = file.OldPath,
            Staged = file.Staged,
            HeadId = file.HeadId,
            IndexId = file.IndexId,
        };
        var diff = await Checked(() => _reader.ReadFileAsync(worktree, request, ct)).ConfigureAwait(false);
        return SidecarMethods.Result(ChangesFile.From(diff));
    }

    /// <summary>Stages files, or one hunk of the unstaged diff, then answers with the new status.</summary>
    public Task<JsonElement> StageAsync(JsonElement? parameters, CancellationToken ct) =>
        ChangeAsync(parameters, new Change((w, paths, t) => w.StageAsync(paths, t), (w, hunk, t) => w.StageHunkAsync(hunk, t)), ct);

    /// <summary>Unstages files, or one hunk of the staged diff, then answers with the new status.</summary>
    public Task<JsonElement> UnstageAsync(JsonElement? parameters, CancellationToken ct) =>
        ChangeAsync(parameters, new Change((w, paths, t) => w.UnstageAsync(paths, t), (w, hunk, t) => w.UnstageHunkAsync(hunk, t)), ct);

    /// <summary>Commits what is staged, or amends the last commit, then answers with the new commit and status.</summary>
    public async Task<JsonElement> CommitAsync(JsonElement? parameters, CancellationToken ct)
    {
        var commit = SidecarMethods.Params<ChangesCommitParams>(parameters);
        var worktree = worktrees.Resolve(commit.RepoId, commit.Worktree);
        var request = new CommitRequest(SidecarMethods.Required(commit.Message, "message"), commit.Amend);
        await Checked(() => new CommitWriter(worktree, outputs.For(runnerOptions, parameters)).CommitAsync(request, ct)).ConfigureAwait(false);
        var status = await _reader.ReadStatusAsync(worktree, ct).ConfigureAwait(false);
        return SidecarMethods.Result(new ChangesCommitResult(status.HeadCommit!, status));
    }

    private async Task<JsonElement> ChangeAsync(JsonElement? parameters, Change change, CancellationToken ct)
    {
        var stage = SidecarMethods.Params<ChangesStageParams>(parameters);
        var worktree = worktrees.Resolve(stage.RepoId, stage.Worktree);
        var writer = new ChangesWriter(worktree, runnerOptions);
        await Checked(() => (stage.Paths, stage.Hunk) switch
        {
            ({ } paths, null) => change.Files(writer, paths, ct),
            (null, { } hunk) => change.Hunk(writer, HunkOf(hunk), ct),
            _ => throw new RpcException(RpcErrorCodes.InvalidParams, "Give either 'paths' or 'hunk'."),
        }).ConfigureAwait(false);
        return SidecarMethods.Result(await _reader.ReadStatusAsync(worktree, ct).ConfigureAwait(false));
    }

    private static HunkRequest HunkOf(ChangesHunkParams hunk) =>
        new(SidecarMethods.Required(hunk.Path, "hunk.path"), hunk.OldPath, SidecarMethods.Required(hunk.Id, "hunk.id"));

    /// <summary>A path outside the worktree, a folder, or an id that is not one, is the caller's mistake.</summary>
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

    private static Task<bool> Checked(Func<Task> run) => Checked(async () =>
    {
        await run().ConfigureAwait(false);
        return true;
    });

    /// <summary>The two halves of staging or of unstaging.</summary>
    private sealed record Change(
        Func<ChangesWriter, IReadOnlyList<string>, CancellationToken, Task> Files,
        Func<ChangesWriter, HunkRequest, CancellationToken, Task> Hunk);
}
