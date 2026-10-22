using System.Globalization;

namespace Diffract.Git.Parsing;

/// <summary>Parses <c>git status --porcelain=v2 --branch -z</c>.</summary>
public static class StatusParser
{
    public static IReadOnlyList<string> Arguments { get; } = ["status", "--porcelain=v2", "--branch", "-z"];

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
            '1' => Tracked(field, StatusEntryKind.Changed, partCount: 9, originalPath: null),
            '2' => Renamed(fields, ref i),
            'u' => Tracked(field, StatusEntryKind.Unmerged, partCount: 11, originalPath: null),
            '?' => new StatusEntry(StatusEntryKind.Untracked, '?', '?', field[2..], null),
            '!' => new StatusEntry(StatusEntryKind.Ignored, '!', '!', field[2..], null),
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
        return new StatusEntry(kind, parts[1][0], parts[1][1], parts[^1], originalPath);
    }

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
