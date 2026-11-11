namespace Diffract.Git.Refs;

/// <summary>
/// The branches a remote has right now, asked with <c>git ls-remote --heads</c>. It reads from the network and
/// changes nothing, here or there. The runner needs the network timeout, as fetch has.
/// </summary>
public static class RemoteHeads
{
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>A branch lookup may wait on a slow network or a sign-in window, like a fetch.</summary>
    public static readonly TimeSpan Patience = Changes.RemoteSync.NetworkPatience;

    /// <summary>The runner to ask with: read-only, with the network timeout.</summary>
    public static GitRunner RunnerFor(string repoRoot, GitRunnerOptions options) =>
        new(repoRoot, options with { ChangesOnly = false, CleanupOnly = false, ReadOnly = true, Timeout = Patience });

    /// <summary>The names of the remote's branches, such as "main" and "fix-1". Throws when the remote cannot be reached.</summary>
    public static async Task<IReadOnlySet<string>> ReadAsync(GitRunner reads, string remote, CancellationToken ct)
    {
        var result = await reads.RunAsync(["ls-remote", "--heads", remote], [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new RemoteFailedException(
                $"Git could not ask {remote} which branches it has, so nothing was removed. Check the network, and that you can sign in. This is what git said:",
                GitOutputSplitter.Readable(result.StandardError));
        }

        return Parse(result.StandardOutput);
    }

    /// <summary>Lines of "&lt;id&gt;&lt;tab&gt;refs/heads/&lt;name&gt;". Anything else is not a branch.</summary>
    public static IReadOnlySet<string> Parse(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('\t', 2))
        .Where(parts => parts.Length == 2 && parts[1].StartsWith(HeadsPrefix, StringComparison.Ordinal))
        .Select(parts => parts[1][HeadsPrefix.Length..])
        .ToHashSet(StringComparer.Ordinal);
}
