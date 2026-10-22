using Diffract.Git.Snapshot;

namespace Diffract.Git.Changes;

/// <summary>
/// Runs a write that needs the worktree's index lock. While another git process, usually an agent, holds it, the
/// write is tried again for up to <see cref="Patience"/>, then given up with <see cref="IndexLockedException"/>.
/// Whether the index is locked is read from the lock file, never from git's message. The lock is never touched.
/// </summary>
public sealed class IndexLockRetry(TimeProvider time)
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    public async Task<GitResult> RunAsync(GitRunner runner, GitCommandLine command, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            try
            {
                return await runner.RunAsync(command, ct).ConfigureAwait(false);
            }
            catch (GitCommandException) when (IsLocked(runner.WorkingDirectory))
            {
                if (time.GetElapsedTime(started) >= Patience)
                    throw new IndexLockedException();
                await Task.Delay(Pause, time, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLocked(string worktree) => GitDirectory.FindIndexLock(worktree) is { } lockFile && File.Exists(lockFile);
}
