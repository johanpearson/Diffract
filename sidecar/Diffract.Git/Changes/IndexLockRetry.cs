using Diffract.Git.Snapshot;

namespace Diffract.Git.Changes;

/// <summary>
/// Runs a write that needs the worktree's index lock. While another git process, usually an agent, holds it, the
/// write is tried again for up to <see cref="Patience"/>, then given up with <see cref="IndexLockedException"/>.
/// Whether the index is locked is read from the lock file, never from git's message: a failure counts as the lock's
/// when the file was there just before git ran or just after it failed, as the holder may let go in between. The
/// lock is never touched.
/// </summary>
public sealed class IndexLockRetry(TimeProvider time)
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    public Task<GitResult> RunAsync(GitRunner runner, GitCommandLine command, CancellationToken ct) =>
        RunAsync(runner, command, [], ct);

    /// <summary>
    /// As the other RunAsync, with exit codes that are returned rather than thrown. A failure while the index is
    /// locked is tried again whether it was thrown or returned.
    /// </summary>
    public async Task<GitResult> RunAsync(GitRunner runner, GitCommandLine command, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var attempt = new Attempt(runner, command, allowedExitCodes);
        while (true)
        {
            var lockedBefore = IsLocked(runner.WorkingDirectory);
            var result = await TryAsync(attempt, lockedBefore, ct).ConfigureAwait(false);
            if (IsFinal(result, lockedBefore || IsLocked(runner.WorkingDirectory)))
                return result!;
            if (time.GetElapsedTime(started) >= Patience)
                throw new IndexLockedException();
            await Task.Delay(Pause, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Success, or a failure that is not down to the lock.</summary>
    private static bool IsFinal(GitResult? result, bool locked) =>
        result is not null && (result.ExitCode == 0 || !locked);

    /// <summary>The result, or null when git failed while the index was locked.</summary>
    private static async Task<GitResult?> TryAsync(Attempt attempt, bool lockedBefore, CancellationToken ct)
    {
        try
        {
            return await attempt.Runner.RunAsync(attempt.Command, attempt.AllowedExitCodes, ct).ConfigureAwait(false);
        }
        catch (GitCommandException) when (lockedBefore || IsLocked(attempt.Runner.WorkingDirectory))
        {
            return null;
        }
    }

    private static bool IsLocked(string worktree) => GitDirectory.FindIndexLock(worktree) is { } lockFile && File.Exists(lockFile);

    /// <summary>One git call that may be tried again.</summary>
    private sealed record Attempt(GitRunner Runner, GitCommandLine Command, IReadOnlyCollection<int> AllowedExitCodes);
}
