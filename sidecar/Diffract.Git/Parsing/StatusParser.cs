using System.Globalization;

namespace Diffract.Git.Parsing;

/// <summary>Parses <c>git status --porcelain=v2 --branch -z</c>.</summary>
public static class StatusParser
{
    /// <summary>A folder of untracked files is one entry, which is all a worktree's file count needs.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["status", "--porcelain=v2", "--branch", "-z"];

    /// <summary>
    /// Every untracked file on its own, so a new file in a new folder can be staged by itself. A folder is then listed
    /// only when it holds a repo of its own, with a trailing slash.
    /// </summary>
    public static IReadOnlyList<string> ChangesArguments { get; } = ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"];

    public static StatusReport Parse(string output)
    {
        var header = new BranchHeader();
        var entries = new List<StatusEntry>();

        var fields = output.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length == 0)
                continue;

            if (field[0] == '#')
                header.Read(field);
            else
                entries.Add(ParseEntry(fields, ref i));
        }

        return new StatusReport(header.ToBranch(), entries);
    }

    private static StatusEntry ParseEntry(string[] fields, ref int i)
    {
        var field = fields[i];
        return field[0] switch
        {
            '1' => WithIds(Tracked(field, StatusEntryKind.Changed, partCount: 9, originalPath: null), field),
            '2' => WithIds(Renamed(fields, ref i), field),
            'u' => Tracked(field, StatusEntryKind.Unmerged, partCount: 11, originalPath: null),
            '?' => Untracked(StatusEntryKind.Untracked, '?', field),
            '!' => Untracked(StatusEntryKind.Ignored, '!', field),
            _ => throw new FormatException($"Unknown status entry: '{field}'"),
        };
    }

    // The original path follows in the next NUL-separated field.
    private static StatusEntry Renamed(string[] fields, ref int i)
    {
        var field = fields[i];
        if (i + 1 >= fields.Length)
            throw new FormatException($"Rename entry without original path: '{field}'");
        return Tracked(field, StatusEntryKind.RenamedOrCopied, partCount: 10, originalPath: fields[++i]);
    }

    // The path is the last space-separated part and may itself contain spaces.
    private static StatusEntry Tracked(string field, StatusEntryKind kind, int partCount, string? originalPath)
    {
        var parts = field.Split(' ', partCount);
        if (parts.Length != partCount || parts[1].Length != 2)
            throw new FormatException($"Malformed status entry: '{field}'");
        return new StatusEntry
        {
            Kind = kind,
            IndexStatus = parts[1][0],
            WorktreeStatus = parts[1][1],
            Path = parts[^1],
            OriginalPath = originalPath,
            IsSubmodule = parts[2][0] == 'S',
        };
    }

    // "? path" and "! path": the same letter in both columns.
    private static StatusEntry Untracked(StatusEntryKind kind, char status, string field) =>
        new() { Kind = kind, IndexStatus = status, WorktreeStatus = status, Path = field[2..] };

    // "1 XY sub mH mI mW hH hI path" and "2 XY sub mH mI mW hH hI Xscore path": the blob ids are the 7th and 8th parts.
    // An id of all zeros means that side has no file.
    private static StatusEntry WithIds(StatusEntry entry, string field)
    {
        var parts = field.Split(' ', 9);
        return entry with { HeadId = BlobOrNull(parts[6]), IndexId = BlobOrNull(parts[7]) };
    }

    private static string? BlobOrNull(string id) => ObjectId.IsZero(id) ? null : ObjectId.Parse(id);

    /// <summary>The "# branch.*" header lines, collected as they come.</summary>
    private sealed class BranchHeader
    {
        private string? _oid;
        private string? _head;
        private string? _upstream;
        private int? _ahead;
        private int? _behind;

        public void Read(string field)
        {
            var parts = field.Split(' ', 3);
            if (parts.Length < 3)
                return;

            var value = parts[2];
            switch (parts[1])
            {
                case "branch.oid":
                    _oid = value == "(initial)" ? null : value;
                    break;
                case "branch.head":
                    _head = value == "(detached)" ? null : value;
                    break;
                case "branch.upstream":
                    _upstream = value;
                    break;
                case "branch.ab":
                    ReadAheadBehind(value);
                    break;
            }
        }

        public StatusBranch ToBranch() => new() { Oid = _oid, Head = _head, Upstream = _upstream, Ahead = _ahead, Behind = _behind };

        private void ReadAheadBehind(string value)
        {
            var counts = value.Split(' ');
            _ahead = int.Parse(counts[0].TrimStart('+'), CultureInfo.InvariantCulture);
            _behind = int.Parse(counts[1].TrimStart('-'), CultureInfo.InvariantCulture);
        }
    }
}
