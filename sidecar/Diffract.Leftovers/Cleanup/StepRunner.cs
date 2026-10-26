using Diffract.Git;

namespace Diffract.Leftovers.Cleanup;

/// <summary>
/// Runs a step whose checks held, through the cleanup runner and nothing else. Returns why it failed, or null when it
/// was done. A backup tag never carries an identity git made up: with no name or email set, the tagger is "Diffract"
/// (decision 1).
/// </summary>
internal sealed class StepRunner(string repoPath, GitRunnerOptions git, IFolderOperations folders)
{
    /// <summary>Git's exit code when <c>worktree remove</c> stopped part way, as when a file was opened after the check.</summary>
    private const int StoppedPartWay = 255;

    private readonly GitRunner _cleanup = new(repoPath, git with { CleanupOnly = true });
    private GitRunner? _tagger;

    public async Task<string?> RunAsync(PendingStep step, Checked check, CancellationToken ct)
    {
        if (step.Command is not { } command)
            return folders.Recycle(step.Folder!);
        try
        {
            var runner = command.Arguments is ["tag", "-a", ..] ? await TaggerAsync(ct).ConfigureAwait(false) : _cleanup;
            await runner.RunAsync(ForNow(command, check), ct).ConfigureAwait(false);
            return null;
        }
        catch (GitCommandException e) when (e.ExitCode == StoppedPartWay && command.Arguments is ["worktree", "remove", _])
        {
            return $"Git stopped part way, as a program is using files in {command.Arguments[^1]}. The folder was left with those files, and shows up as a leftover folder on the next scan. The branch is kept.";
        }
        catch (GitException e)
        {
            return $"Git refused: {Reason(e)}";
        }
    }

    /// <summary>A stash is dropped at its place in the list now, which the checks just found.</summary>
    private static GitCommandLine ForNow(GitCommandLine command, Checked check) =>
        check.StashName is { } stash && command.Arguments is ["stash", "drop", _] ? new GitCommandLine(["stash", "drop", stash], []) : command;

    private static string Reason(GitException e) => e is GitCommandException failed && failed.StandardError.Trim() is { Length: > 0 } said ? said : e.Message;

    private async Task<GitRunner> TaggerAsync(CancellationToken ct)
    {
        _tagger ??= await HasIdentityAsync(ct).ConfigureAwait(false) ? _cleanup : new GitRunner(repoPath, _cleanup.Options with { Environment = AsDiffract() });
        return _tagger;
    }

    /// <summary>Read once per cleanup that makes a backup. Git would otherwise guess a name and email from Windows.</summary>
    private async Task<bool> HasIdentityAsync(CancellationToken ct)
    {
        var reads = new GitRunner(repoPath, StepChecks.ReadOnly(git));
        var name = await reads.RunAsync(["config", "--get", "user.name"], [1], ct).ConfigureAwait(false);
        var email = await reads.RunAsync(["config", "--get", "user.email"], [1], ct).ConfigureAwait(false);
        return name.ExitCode == 0 && email.ExitCode == 0;
    }

    private Dictionary<string, string> AsDiffract() => new(git.Environment)
    {
        ["GIT_COMMITTER_NAME"] = "Diffract",
        ["GIT_COMMITTER_EMAIL"] = "diffract@localhost",
    };
}
