namespace Diffract.Git.Parsing;

public enum StatusEntryKind { Changed, RenamedOrCopied, Unmerged, Untracked, Ignored }

/// <summary>The branch header of <c>git status</c>.</summary>
public sealed record StatusBranch
{
    /// <summary>Commit at HEAD, or null before the first commit.</summary>
    public string? Oid { get; init; }

    /// <summary>Branch name, or null when HEAD is detached.</summary>
    public string? Head { get; init; }

    public string? Upstream { get; init; }

    /// <summary>Commits ahead of upstream, or null without an upstream (or when it is gone).</summary>
    public int? Ahead { get; init; }

    public int? Behind { get; init; }

    public bool IsDetached => Head is null;
}

/// <param name="IndexStatus">X in porcelain v2: '.', 'M', 'A', 'D', 'R', 'C', 'U' and so on. '?' or '!' for untracked and ignored.</param>
/// <param name="OriginalPath">Path before a rename or copy.</param>
public sealed record StatusEntry(StatusEntryKind Kind, char IndexStatus, char WorktreeStatus, string Path, string? OriginalPath)
{
    /// <summary>The file's blob at HEAD. Null when HEAD has no such file, and for untracked, ignored and unmerged entries.</summary>
    public string? HeadId { get; init; }

    /// <summary>The file's blob in the index. Null when the index has none, as for a deleted or intent-to-add file.</summary>
    public string? IndexId { get; init; }

    /// <summary>The entry is a submodule, whose content is a commit in another repo.</summary>
    public bool IsSubmodule { get; init; }
}

/// <summary>What <see cref="StatusParser"/> reads from <c>git status</c>.</summary>
public sealed record StatusReport(StatusBranch Branch, IReadOnlyList<StatusEntry> Entries);
