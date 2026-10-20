namespace Diffract.Git.Snapshot;

public sealed record SnapshotOptions
{
    /// <summary>"auto", or the name of the main branch.</summary>
    public string MainBranch { get; init; } = "auto";

    /// <summary>Folders matching these globs have their subfolders checked for leftovers.</summary>
    public IReadOnlyList<string> WorktreeContainerPatterns { get; init; } = [];

    /// <summary>
    /// Which unmerged refs get a commits-at-risk count, by full ref name. Each one costs a git call,
    /// so the default skips remote-tracking branches, which can number in the hundreds.
    /// </summary>
    public Func<string, bool> AnalyzeRisk { get; init; } = DefaultAnalyzeRisk;

    /// <summary>Run <c>git fsck</c> to find unreachable commits. It is the slowest call by far.</summary>
    public bool IncludeUnreachable { get; init; } = true;

    public int MaxParallelGitCalls { get; init; } = 8;

    private static bool DefaultAnalyzeRisk(string fullName) =>
        fullName.StartsWith("refs/heads/", StringComparison.Ordinal) || fullName.StartsWith("refs/tags/", StringComparison.Ordinal);
}
