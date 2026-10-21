using System.Globalization;
using System.Text;

namespace Diffract.Git.Parsing;

public enum FileChangeKind { Added, Deleted, Modified, Renamed, Copied, TypeChanged, Unknown }

/// <summary>One changed file in a commit, compared with its first parent.</summary>
/// <param name="OldPath">The path before a rename or copy. Null otherwise.</param>
/// <param name="OldId">The blob before the change. Null when the file was added.</param>
/// <param name="NewId">The blob after the change. Null when the file was deleted.</param>
/// <param name="Similarity">How alike the two sides of a rename or copy are, 0 to 100. Null otherwise.</param>
/// <param name="Additions">Lines added. Null for binary files.</param>
/// <param name="IsBinary">git sees the file as binary, by its content or by <c>.gitattributes</c>.</param>
/// <param name="IsSubmodule">Either side is a submodule commit, which has no content in this repo.</param>
public sealed record FileChange(
    FileChangeKind Kind,
    string Path,
    string? OldPath,
    string? OldId,
    string? NewId,
    int? Similarity,
    int? Additions,
    int? Deletions,
    bool IsBinary,
    bool IsSubmodule);

/// <summary>Everything the details panel shows about one commit.</summary>
/// <param name="Body">The message after the subject, without the blank line between. Can be empty.</param>
/// <param name="Files">Changes against the first parent. A root commit's files are all added.</param>
public sealed record CommitDetails(
    string Id,
    IReadOnlyList<string> ParentIds,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommittedAt,
    string Subject,
    string Body,
    IReadOnlyList<FileChange> Files);

/// <summary>
/// Parses <c>git diff-tree -z</c> with <see cref="Format"/>, <c>--raw</c> and <c>--numstat</c>: the commit's ten
/// NUL-terminated fields, then one raw entry per file, then one numstat entry per file in the same order.
/// </summary>
public static class CommitDetailsParser
{
    private const int HeaderFields = 10;
    private const string SubmoduleMode = "160000";

    public const string Format = "%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%s%x00%b";

    /// <summary>
    /// --always prints the commit even when it changes nothing, --root lists a root commit's files as added,
    /// and --diff-merges=first-parent compares a merge with its first parent, as the history list follows it.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string commitId) =>
        ["diff-tree", "-r", "-z", "--always", "--root", "-M", "--diff-merges=first-parent", "--raw", "--numstat",
            "--format=" + Format, ObjectId.Require(commitId)];

    public static CommitDetails Parse(byte[] output)
    {
        var f = Encoding.UTF8.GetString(output).Split('\0');
        if (f.Length < HeaderFields)
            throw new FormatException($"Expected {HeaderFields} commit fields, got {f.Length}.");

        // After the format's own NUL, git writes a newline before the first file, and the output ends with a NUL.
        var files = f.Skip(HeaderFields).ToList();
        if (files.Count > 0 && files[^1].Length == 0)
            files.RemoveAt(files.Count - 1);
        if (files.Count > 0 && files[0].StartsWith('\n'))
            files[0] = files[0][1..];
        var rest = new Queue<string>(files);

        var raw = ReadRaw(rest);
        var numstat = ReadNumstat(rest, raw.Count);

        return new CommitDetails(
            Id: ObjectId.Parse(f[0]),
            ParentIds: f[1].Length == 0 ? [] : f[1].Split(' ').Select(ObjectId.Parse).ToArray(),
            AuthorName: f[2],
            AuthorEmail: f[3],
            AuthoredAt: IsoTime(f[4]),
            CommitterName: f[5],
            CommitterEmail: f[6],
            CommittedAt: IsoTime(f[7]),
            Subject: f[8],
            Body: f[9].TrimEnd('\n'),
            Files: raw.Select((r, i) => r with { Additions = numstat[i].Additions, Deletions = numstat[i].Deletions, IsBinary = numstat[i].IsBinary }).ToList());
    }

    // ":<old mode> <new mode> <old id> <new id> <status>", then the path, and the new path for renames and copies.
    private static List<FileChange> ReadRaw(Queue<string> tokens)
    {
        List<FileChange> files = [];
        while (tokens.TryPeek(out var token) && token.StartsWith(':'))
        {
            tokens.Dequeue();
            var p = token[1..].Split(' ');
            if (p.Length != 5 || p[4].Length == 0)
                throw new FormatException($"Unexpected raw diff entry: '{token}'.");

            var kind = p[4][0] switch
            {
                'A' => FileChangeKind.Added,
                'D' => FileChangeKind.Deleted,
                'M' => FileChangeKind.Modified,
                'R' => FileChangeKind.Renamed,
                'C' => FileChangeKind.Copied,
                'T' => FileChangeKind.TypeChanged,
                _ => FileChangeKind.Unknown,
            };
            var twoPaths = kind is FileChangeKind.Renamed or FileChangeKind.Copied;
            var first = Next(tokens, token);
            var path = twoPaths ? Next(tokens, token) : first;

            files.Add(new FileChange(
                Kind: kind,
                Path: path,
                OldPath: twoPaths ? first : null,
                OldId: Blob(p[2]),
                NewId: Blob(p[3]),
                Similarity: twoPaths && int.TryParse(p[4].AsSpan(1), CultureInfo.InvariantCulture, out var score) ? score : null,
                Additions: null,
                Deletions: null,
                IsBinary: false,
                IsSubmodule: p[0] == SubmoduleMode || p[1] == SubmoduleMode));
        }
        return files;
    }

    // "<added>\t<deleted>\t<path>", or "<added>\t<deleted>\t" followed by the old and new path. "-" for binary files.
    private static List<(int? Additions, int? Deletions, bool IsBinary)> ReadNumstat(Queue<string> tokens, int count)
    {
        List<(int?, int?, bool)> stats = [];
        for (var i = 0; i < count; i++)
        {
            var token = tokens.Count > 0 ? tokens.Dequeue() : throw new FormatException($"Expected {count} numstat entries, got {i}.");
            var p = token.Split('\t', 3);
            if (p.Length != 3)
                throw new FormatException($"Unexpected numstat entry: '{token}'.");
            if (p[2].Length == 0)
            {
                Next(tokens, token);
                Next(tokens, token);
            }

            var binary = p[0] == "-" && p[1] == "-";
            stats.Add((binary ? null : Count(p[0]), binary ? null : Count(p[1]), binary));
        }

        if (tokens.Count > 0)
            throw new FormatException($"Unexpected output after the changed files: '{tokens.Peek()}'.");
        return stats;
    }

    private static string Next(Queue<string> tokens, string entry) =>
        tokens.Count > 0 ? tokens.Dequeue() : throw new FormatException($"A path is missing after '{entry}'.");

    private static string? Blob(string id) => ObjectId.IsZero(id) ? null : ObjectId.Parse(id);

    private static int Count(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"Expected a line count, got '{value}'.");

    private static DateTimeOffset IsoTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new FormatException($"Expected a date, got '{value}'.");
}
