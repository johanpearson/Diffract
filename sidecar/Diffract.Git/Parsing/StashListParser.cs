using System.Globalization;

namespace Diffract.Git.Parsing;

/// <param name="Name">Reflog selector, for example "stash@{0}".</param>
/// <param name="Message">Reflog subject, for example "On main: autostash" or "WIP on main: abc1234 Subject".</param>
public sealed record StashEntry(int Index, string Name, string CommitId, DateTimeOffset CreatedAt, string Message);

/// <summary>Parses <c>git stash list -z</c> with <see cref="Format"/>: NUL between stashes, 0x1F between fields.</summary>
public static class StashListParser
{
    // The message goes last so a stray separator inside it cannot shift other fields.
    public const string Format = "%gd%x1f%H%x1f%ct%x1f%gs";

    public static IReadOnlyList<string> Arguments { get; } = ["stash", "list", "-z", "--format=" + Format];

    public static IReadOnlyList<StashEntry> Parse(string output)
    {
        var entries = new List<StashEntry>();
        foreach (var record in output.Split('\0'))
        {
            if (record.Length == 0)
                continue;

            var f = record.Split('\x1f', 4);
            if (f.Length != 4)
                throw new FormatException($"Expected 4 fields in stash record: '{record}'");

            entries.Add(new StashEntry(
                ParseIndex(f[0]),
                f[0],
                f[1],
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[2], CultureInfo.InvariantCulture)),
                f[3]));
        }
        return entries;
    }

    private static int ParseIndex(string name)
    {
        var open = name.IndexOf("@{", StringComparison.Ordinal);
        if (open < 0 || !name.EndsWith('}'))
            throw new FormatException($"Unexpected stash name: '{name}'");
        return int.Parse(name.AsSpan(open + 2, name.Length - open - 3), CultureInfo.InvariantCulture);
    }
}
