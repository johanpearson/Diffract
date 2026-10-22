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

/// <summary>One entry of <c>git status</c>.</summary>
public sealed record StatusEntry
{
    public required StatusEntryKind Kind { get; init; }

    /// <summary>X in porcelain v2: '.', 'M', 'A', 'D', 'R', 'C', 'U' and so on. '?' or '!' for untracked and ignored.</summary>
    public required char IndexStatus { get; init; }

    /// <summary>Y in porcelain v2, with the same letters.</summary>
    public required char WorktreeStatus { get; init; }

    public required string Path { get; init; }

    /// <summary>Path before a rename or copy.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>The file's blob at HEAD. Null when HEAD has no such file, and for untracked, ignored and unmerged entries.</summary>
    public string? HeadId { get; init; }

    /// <summary>The file's blob in the index. Null when the index has none, as for a deleted or intent-to-add file.</summary>
    public string? IndexId { get; init; }

    /// <summary>The entry is a submodule, whose content is a commit in another repo.</summary>
    public bool IsSubmodule { get; init; }
}

/// <summary>What <see cref="StatusParser"/> reads from <c>git status</c>.</summary>
public sealed record StatusReport(StatusBranch Branch, IReadOnlyList<StatusEntry> Entries);
