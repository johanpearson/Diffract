using System.Globalization;

namespace Diffract.Git.Parsing;

public sealed record UpstreamTracking(bool IsGone, int Ahead, int Behind)
{
    public static UpstreamTracking None { get; } = new(false, 0, 0);
}

/// <param name="ObjectType">"commit" for most refs, "tag" for annotated tags.</param>
/// <param name="PeeledObjectId">For annotated tags, the object the tag points to.</param>
/// <param name="Upstream">Full upstream ref name for local branches that track one.</param>
/// <param name="CreatorDate">Committer date for commits, tagger date for annotated tags.</param>
/// <param name="SymrefTarget">Target ref for symbolic refs such as refs/remotes/origin/HEAD.</param>
public sealed record RefEntry(
    string Name,
    string ObjectId,
    string ObjectType,
    string? PeeledObjectId,
    string? Upstream,
    UpstreamTracking Tracking,
    DateTimeOffset? CreatorDate,
    string? SymrefTarget)
{
    /// <summary>The commit (or other object) the ref finally points at.</summary>
    public string TargetId => PeeledObjectId ?? ObjectId;
}

/// <summary>Parses <c>git for-each-ref</c> with <see cref="Format"/>: NUL between fields, one ref per line.</summary>
public static class RefListParser
{
    private const int FieldCount = 8;

    public const string Format =
        "%(refname)%00%(objectname)%00%(objecttype)%00%(*objectname)%00" +
        "%(upstream)%00%(upstream:track,nobracket)%00%(creatordate:unix)%00%(symref)";

    public static IReadOnlyList<string> Arguments { get; } = ["for-each-ref", "--format=" + Format];

    // Ref names cannot contain NUL or newline, and no field in the format can either.
    public static IReadOnlyList<RefEntry> Parse(string output)
    {
        var entries = new List<RefEntry>();
        foreach (var line in output.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            var f = line.Split('\0');
            if (f.Length != FieldCount)
                throw new FormatException($"Expected {FieldCount} fields in ref line, got {f.Length}: '{line.Replace('\0', '|')}'");

            entries.Add(new RefEntry(
                Name: f[0],
                ObjectId: f[1],
                ObjectType: f[2],
                PeeledObjectId: NullIfEmpty(f[3]),
                Upstream: NullIfEmpty(f[4]),
                Tracking: ParseTracking(f[5]),
                CreatorDate: f[6].Length == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[6], CultureInfo.InvariantCulture)),
                SymrefTarget: NullIfEmpty(f[7])));
        }
        return entries;
    }

    // "", "gone", "ahead 2", "behind 1" or "ahead 2, behind 1".
    internal static UpstreamTracking ParseTracking(string track)
    {
        if (track.Length == 0)
            return UpstreamTracking.None;
        if (track == "gone")
            return new UpstreamTracking(true, 0, 0);

        int ahead = 0, behind = 0;
        foreach (var part in track.Split(", "))
        {
            var words = part.Split(' ');
            if (words.Length != 2)
                throw new FormatException($"Unknown upstream tracking text: '{track}'");
            var count = int.Parse(words[1], CultureInfo.InvariantCulture);
            if (words[0] == "ahead")
                ahead = count;
            else if (words[0] == "behind")
                behind = count;
            else
                throw new FormatException($"Unknown upstream tracking text: '{track}'");
        }
        return new UpstreamTracking(false, ahead, behind);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
