namespace Diffract.Git.Refs;

/// <summary>
/// The branches a remote has right now, and which one is its default, asked with <c>git ls-remote</c>. It reads from
/// the network and changes nothing, here or there. The runner needs the network timeout, as fetch has.
/// </summary>
public static class RemoteHeads
{
    /// <summary>A branch lookup may wait on a slow network or a sign-in window, like a fetch.</summary>
    public static readonly TimeSpan Patience = Changes.RemoteSync.NetworkPatience;

    /// <summary>The runner to ask with: read-only, with the network timeout.</summary>
    public static GitRunner RunnerFor(string repoRoot, GitRunnerOptions options) =>
        new(repoRoot, options with { ChangesOnly = false, CleanupOnly = false, ReadOnly = true, Timeout = Patience });

    /// <summary>The names of the remote's branches, such as "main" and "fix-1". Throws when the remote cannot be reached.</summary>
    public static async Task<IReadOnlySet<string>> ReadAsync(GitRunner reads, string remote, CancellationToken ct) =>
        (await ReadWithIdsAsync(reads, remote, ct).ConfigureAwait(false)).Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The remote's branches, each with the commit it is at. Throws when the remote cannot be reached.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadWithIdsAsync(GitRunner reads, string remote, CancellationToken ct)
    {
        var result = await reads.RunAsync(["ls-remote", "--heads", remote], [1, 128], ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? new RemoteListing(result.StandardOutput).Heads : throw Unreachable(remote, result);
    }

    /// <summary>
    /// The branch the remote's HEAD points at, which is its default branch, such as "main". Null when the remote
    /// reports none. Throws when the remote cannot be reached.
    /// </summary>
    public static async Task<string?> ReadDefaultAsync(GitRunner reads, string remote, CancellationToken ct)
    {
        var result = await reads.RunAsync(["ls-remote", "--symref", remote, "HEAD"], [1, 128], ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? new RemoteListing(result.StandardOutput).DefaultBranch : throw Unreachable(remote, result);
    }

    private static RemoteFailedException Unreachable(string remote, GitResult result) => new(
        $"Git could not ask {remote} which branches it has, so nothing was removed. Check the network, and that you can sign in. This is what git said:",
        GitOutputSplitter.Readable(result.StandardError));

    /// <summary>Lines of "&lt;id&gt;&lt;tab&gt;refs/heads/&lt;name&gt;". Anything else is not a branch.</summary>
    public static IReadOnlySet<string> Parse(string output) => new RemoteListing(output).Heads.Keys.ToHashSet(StringComparer.Ordinal);
}
