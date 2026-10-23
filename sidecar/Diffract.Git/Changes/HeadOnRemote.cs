namespace Diffract.Git.Changes;

/// <summary>
/// The remote-tracking branch that already has the commit at HEAD. Amending a commit that one has would rewrite
/// pushed work, so amend is allowed only when there is none.
/// </summary>
public static class HeadOnRemote
{
    private const string Remotes = "refs/remotes/";

    public static IReadOnlyList<string> Arguments { get; } = ["for-each-ref", "--contains", "HEAD", "--format=%(refname)", Remotes];

    /// <summary>
    /// Reads it alongside status. Before the first commit there is no HEAD and git fails, with an exit code that
    /// differs between versions, so the failure is kept, not thrown: status says whether there is a commit, and only
    /// then does the answer count.
    /// </summary>
    public static async Task<Answer> ReadAsync(GitRunner runner, CancellationToken ct)
    {
        try
        {
            return new Answer(await runner.RunAsync(Arguments, ct).ConfigureAwait(false), null);
        }
        catch (GitCommandException e)
        {
            return new Answer(null, e);
        }
    }

    /// <summary>The first such branch, as "origin/main". Symbolic refs such as origin/HEAD are passed over. Null when none has it.</summary>
    public static string? Parse(GitResult result) =>
        result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(r => r.StartsWith(Remotes, StringComparison.Ordinal) && !r.EndsWith("/HEAD", StringComparison.Ordinal))
            .Select(r => r[Remotes.Length..])
            .FirstOrDefault();

    /// <summary>What git answered, or how it failed.</summary>
    public sealed record Answer(GitResult? Result, GitCommandException? Failure)
    {
        /// <summary>The branch, for a repo with a commit. A failure then is a real one, and is thrown.</summary>
        public string? Name() => Result is { } result ? Parse(result) : throw Failure!;
    }
}
