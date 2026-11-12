namespace Diffract.Git.Refs;

/// <summary>
/// What <c>git ls-remote</c> printed: lines of "&lt;id&gt;&lt;tab&gt;&lt;ref&gt;". Read as the remote's branches with the
/// commit each is at, and as the branch its HEAD points at, which is its default.
/// </summary>
public sealed record RemoteListing(string Output)
{
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>Each branch by name, such as "main" and "fix-1", with its commit. Tags and anything else are not branches.</summary>
    public IReadOnlyDictionary<string, string> Heads => Lines
        .Where(parts => parts[1].StartsWith(HeadsPrefix, StringComparison.Ordinal))
        .ToDictionary(parts => parts[1][HeadsPrefix.Length..], parts => parts[0], StringComparer.Ordinal);

    /// <summary>
    /// The branch the line "ref: refs/heads/&lt;name&gt;&lt;tab&gt;HEAD" names, such as "main". Null when there is none, as
    /// for a bare repo with no commits.
    /// </summary>
    public string? DefaultBranch => Lines
        .Where(parts => parts[1] == "HEAD" && parts[0].StartsWith("ref: " + HeadsPrefix, StringComparison.Ordinal))
        .Select(parts => parts[0]["ref: ".Length..][HeadsPrefix.Length..])
        .FirstOrDefault();

    private IEnumerable<string[]> Lines => Output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('\t', 2))
        .Where(parts => parts.Length == 2);
}
