namespace Diffract.Git.Parsing;

public sealed record UpstreamTracking(bool IsGone, int Ahead, int Behind)
{
    public static UpstreamTracking None { get; } = new(false, 0, 0);
}

/// <summary>One ref as <see cref="RefListParser"/> reads it.</summary>
public sealed record RefEntry
{
    public required string Name { get; init; }

    public required string ObjectId { get; init; }

    /// <summary>"commit" for most refs, "tag" for annotated tags.</summary>
    public required string ObjectType { get; init; }

    /// <summary>For annotated tags, the object the tag points to.</summary>
    public string? PeeledObjectId { get; init; }

    /// <summary>Full upstream ref name for local branches that track one.</summary>
    public string? Upstream { get; init; }

    public required UpstreamTracking Tracking { get; init; }

    /// <summary>Committer date for commits, tagger date for annotated tags.</summary>
    public DateTimeOffset? CreatorDate { get; init; }

    /// <summary>Target ref for symbolic refs such as refs/remotes/origin/HEAD.</summary>
    public string? SymrefTarget { get; init; }

    /// <summary>The commit (or other object) the ref finally points at.</summary>
    public string TargetId => PeeledObjectId ?? ObjectId;
}
