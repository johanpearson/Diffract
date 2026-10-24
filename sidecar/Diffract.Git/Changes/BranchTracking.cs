namespace Diffract.Git.Changes;

/// <summary>The checked-out branch and the remote branch it follows, as git's config has them.</summary>
public sealed record BranchTracking
{
    private const string Heads = "refs/heads/";

    /// <summary>
    /// Every local branch with its upstream, NUL-separated. "*" marks the one checked out in this worktree, so a
    /// detached HEAD or a branch with no commit yet has none.
    /// </summary>
    public static IReadOnlyList<string> Arguments { get; } =
        ["for-each-ref", "--format=%(HEAD)%00%(refname)%00%(upstream:remotename)%00%(upstream:remoteref)%00%(upstream)%00%(upstream:short)", Heads];

    /// <summary>Such as "main".</summary>
    public required string Branch { get; init; }

    /// <summary>The remote's name, such as "origin". Null without an upstream.</summary>
    public string? Remote { get; init; }

    /// <summary>The branch on the remote, such as "refs/heads/main". Null without an upstream.</summary>
    public string? RemoteRef { get; init; }

    /// <summary>The remote-tracking ref, such as "refs/remotes/origin/main". Null without an upstream.</summary>
    public string? Upstream { get; init; }

    /// <summary>As people say it, such as "origin/main". Null without an upstream.</summary>
    public string? UpstreamName { get; init; }

    /// <summary>True when the upstream is a branch on a remote, not another local branch.</summary>
    public bool HasRemoteUpstream => Remote is { Length: > 0 } and not "." && Upstream is not null;

    /// <summary>True when the branch on the remote has the same name, so a push needs no "source:target".</summary>
    public bool SameNameOnRemote => RemoteRef == Heads + Branch;

    /// <summary>The checked-out branch, or null when HEAD is detached or has no commit yet.</summary>
    public static BranchTracking? Parse(string output) =>
        output.Split('\n').Select(line => line.Split('\0')).Where(f => f.Length == 6 && f[0] == "*").Select(From).FirstOrDefault();

    public static async Task<BranchTracking?> ReadAsync(GitRunner runner, CancellationToken ct) =>
        Parse((await runner.RunAsync(Arguments, ct).ConfigureAwait(false)).StandardOutput);

    private static BranchTracking From(string[] f) => new()
    {
        Branch = f[1].StartsWith(Heads, StringComparison.Ordinal) ? f[1][Heads.Length..] : f[1],
        Remote = NullIfEmpty(f[2]),
        RemoteRef = NullIfEmpty(f[3]),
        Upstream = NullIfEmpty(f[4]),
        UpstreamName = NullIfEmpty(f[5]),
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
