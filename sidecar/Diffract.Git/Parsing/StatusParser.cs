using System.Globalization;

namespace Diffract.Git.Parsing;

public enum StatusEntryKind { Changed, RenamedOrCopied, Unmerged, Untracked, Ignored }

/// <param name="Oid">Commit at HEAD, or null before the first commit.</param>
/// <param name="Head">Branch name, or null when HEAD is detached.</param>
/// <param name="Ahead">Commits ahead of upstream, or null without an upstream (or when it is gone).</param>
public sealed record StatusBranch(string? Oid, string? Head, string? Upstream, int? Ahead, int? Behind)
{
    public bool IsDetached => Head is null;
}

/// <param name="IndexStatus">X in porcelain v2: '.', 'M', 'A', 'D', 'R', 'C', 'U' and so on. '?' or '!' for untracked and ignored.</param>
/// <param name="OriginalPath">Path before a rename or copy.</param>
public sealed record StatusEntry(StatusEntryKind Kind, char IndexStatus, char WorktreeStatus, string Path, string? OriginalPath);

public sealed record StatusReport(StatusBranch Branch, IReadOnlyList<StatusEntry> Entries);

/// <summary>Parses <c>git status --porcelain=v2 --branch -z</c>.</summary>
public static class StatusParser
{
    public static IReadOnlyList<string> Arguments { get; } = ["status", "--porcelain=v2", "--branch", "-z"];

    public static StatusReport Parse(string output)
    {
        string? oid = null, head = null, upstream = null;
        int? ahead = null, behind = null;
        var entries = new List<StatusEntry>();

        var fields = output.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length == 0)
                continue;

            switch (field[0])
            {
                case '#':
                    ParseHeader(field, ref oid, ref head, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                    entries.Add(Tracked(field, StatusEntryKind.Changed, partCount: 9, originalPath: null));
                    break;
                case '2':
                    // The original path follows in the next NUL-separated field.
                    if (i + 1 >= fields.Length)
                        throw new FormatException($"Rename entry without original path: '{field}'");
                    entries.Add(Tracked(field, StatusEntryKind.RenamedOrCopied, partCount: 10, originalPath: fields[++i]));
                    break;
                case 'u':
                    entries.Add(Tracked(field, StatusEntryKind.Unmerged, partCount: 11, originalPath: null));
                    break;
                case '?':
                    entries.Add(new StatusEntry(StatusEntryKind.Untracked, '?', '?', field[2..], null));
                    break;
                case '!':
                    entries.Add(new StatusEntry(StatusEntryKind.Ignored, '!', '!', field[2..], null));
                    break;
                default:
                    throw new FormatException($"Unknown status entry: '{field}'");
            }
        }

        return new StatusReport(new StatusBranch(oid, head, upstream, ahead, behind), entries);
    }

    // The path is the last space-separated part and may itself contain spaces.
    private static StatusEntry Tracked(string field, StatusEntryKind kind, int partCount, string? originalPath)
    {
        var parts = field.Split(' ', partCount);
        if (parts.Length != partCount || parts[1].Length != 2)
            throw new FormatException($"Malformed status entry: '{field}'");
        return new StatusEntry(kind, parts[1][0], parts[1][1], parts[^1], originalPath);
    }

    private static void ParseHeader(string field, ref string? oid, ref string? head, ref string? upstream, ref int? ahead, ref int? behind)
    {
        var parts = field.Split(' ', 3);
        if (parts.Length < 3)
            return;

        var value = parts[2];
        switch (parts[1])
        {
            case "branch.oid":
                oid = value == "(initial)" ? null : value;
                break;
            case "branch.head":
                head = value == "(detached)" ? null : value;
                break;
            case "branch.upstream":
                upstream = value;
                break;
            case "branch.ab":
                var counts = value.Split(' ');
                ahead = int.Parse(counts[0].TrimStart('+'), CultureInfo.InvariantCulture);
                behind = int.Parse(counts[1].TrimStart('-'), CultureInfo.InvariantCulture);
                break;
        }
    }
}
