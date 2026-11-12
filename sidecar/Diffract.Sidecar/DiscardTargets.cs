using Diffract.Git;
using Diffract.Git.Changes;
using Diffract.Git.Parsing;
using Diffract.Leftovers.Cleanup;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// Turns what the Changes view picked into what a discard will throw away. Reads the worktree's status, or the hunk's
/// diff, so a file with nothing unstaged, a conflict or a hunk that has changed is refused here, before any dialog.
/// </summary>
internal sealed class DiscardTargets(GitRunnerOptions runnerOptions)
{
    /// <summary>Whole files: an untracked file is deleted, a tracked one goes back to the index. One git call.</summary>
    public async Task<IReadOnlyList<DiscardTarget>> FilesAsync(string worktree, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0)
            throw new RpcException(RpcErrorCodes.InvalidParams, "'paths' must list at least one file");
        var files = (await new ChangesReader(runnerOptions).ReadStatusAsync(worktree, ct).ConfigureAwait(false)).Files;
        return [.. paths.Select(path => File(worktree, path, files.FirstOrDefault(f => f.Path == path)))];
    }

    /// <summary>One hunk of the unstaged diff, as the user saw it. Two git calls.</summary>
    public async Task<IReadOnlyList<DiscardTarget>> HunkAsync(string worktree, ChangesHunkParams hunk, CancellationToken ct)
    {
        var request = new HunkRequest(SidecarMethods.Required(hunk.Path, "hunk.path"), hunk.OldPath, SidecarMethods.Required(hunk.Id, "hunk.id"));
        var found = await new ChangesWriter(worktree, runnerOptions).ReadHunkPatchAsync(request, ct).ConfigureAwait(false);
        return [new DiscardTarget(worktree, request.Path, DiscardMode.ReverseHunk, found.Patch, Describe(found.Hunk))];
    }

    private static DiscardTarget File(string worktree, string path, ChangedFile? file) => file switch
    {
        null or { Unstaged: null } => throw Refused($"{path} has no unstaged changes to discard. Refresh to see the changes as they are now."),
        { Kind: ChangedFileKind.Untracked } => new DiscardTarget(worktree, path, DiscardMode.DeleteNewFile),
        { Kind: ChangedFileKind.Tracked, Unstaged: not FileChangeKind.Added } => new DiscardTarget(worktree, path, DiscardMode.RestoreFromIndex),
        { Kind: ChangedFileKind.Tracked } => throw Refused($"{path} is only marked to be added. Unstage it first, then discard it."),
        _ => throw Refused($"{path} is a conflict, a submodule or a repo of its own, which Diffract does not discard."),
    };

    private static RpcException Refused(string message) => new(RpcErrorCodes.InvalidParams, message);

    /// <summary>Where the dialog says the change is, and how many lines it swaps, in the words the diff uses.</summary>
    private static string Describe(Hunk hunk)
    {
        var line = hunk.NewLines == 0 ? hunk.NewStart + 1 : Math.Max(hunk.NewStart, 1);
        return $"the change at line {line} ({Effect(hunk)})";
    }

    /// <summary>What reversing the hunk does to the file: lines you added go, lines you removed come back.</summary>
    private static string Effect(Hunk hunk) => (hunk.OldLines, hunk.NewLines) switch
    {
        (0, var added) => $"removes your {Lines(added)}",
        (var removed, 0) => $"puts back {Lines(removed)}",
        var (old, now) => $"replaces your {Lines(now)} with {Lines(old)}",
    };

    private static string Lines(int count) => count == 1 ? "1 line" : $"{count} lines";
}
