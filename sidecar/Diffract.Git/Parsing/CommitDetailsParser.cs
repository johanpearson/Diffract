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

        var tokens = new DiffTokens(f.Skip(HeaderFields));
        var raw = new List<FileChange>();
        while (tokens.NextIsRaw())
            raw.Add(ReadRaw(tokens));
        var files = raw.Select(file => WithNumstat(file, tokens)).ToList();
        tokens.ExpectEnd();

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
            Files: files);
    }

    private static readonly Dictionary<char, FileChangeKind> Kinds = new()
    {
        ['A'] = FileChangeKind.Added,
        ['D'] = FileChangeKind.Deleted,
        ['M'] = FileChangeKind.Modified,
        ['R'] = FileChangeKind.Renamed,
        ['C'] = FileChangeKind.Copied,
        ['T'] = FileChangeKind.TypeChanged,
    };

    // ":<old mode> <new mode> <old id> <new id> <status>", then the path, and the new path for renames and copies.
    private static FileChange ReadRaw(DiffTokens tokens)
    {
        var entry = tokens.Take();
        var p = entry[1..].Split(' ');
        if (p.Length != 5 || p[4].Length == 0)
            throw new FormatException($"Unexpected raw diff entry: '{entry}'.");

        var status = p[4];
        var kind = Kinds.GetValueOrDefault(status[0], FileChangeKind.Unknown);
        var twoPaths = kind is FileChangeKind.Renamed or FileChangeKind.Copied;
        var first = tokens.Take();
        return new FileChange(
            Kind: kind,
            Path: twoPaths ? tokens.Take() : first,
            OldPath: twoPaths ? first : null,
            OldId: Blob(p[2]),
            NewId: Blob(p[3]),
            Similarity: twoPaths ? Similarity(status.AsSpan(1)) : null,
            Additions: null,
            Deletions: null,
            IsBinary: false,
            IsSubmodule: p[0] == SubmoduleMode || p[1] == SubmoduleMode);
    }

    // "<added>\t<deleted>\t<path>", or "<added>\t<deleted>\t" followed by the old and new path. "-" for binary files.
    private static FileChange WithNumstat(FileChange file, DiffTokens tokens)
    {
        var entry = tokens.Take();
        var p = entry.Split('\t', 3);
        if (p.Length != 3)
            throw new FormatException($"Unexpected numstat entry: '{entry}'.");
        if (p[2].Length == 0)
            tokens.Skip(2);

        return p[0] == "-" && p[1] == "-"
            ? file with { IsBinary = true }
            : file with { Additions = Count(p[0]), Deletions = Count(p[1]) };
    }

    // The digits after the letter in "R085": how alike the two sides of a rename or copy are.
    private static int? Similarity(ReadOnlySpan<char> score) =>
        int.TryParse(score, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? Blob(string id) => ObjectId.IsZero(id) ? null : ObjectId.Parse(id);

    private static int Count(ReadOnlySpan<char> value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"Expected a line count, got '{value.ToString()}'.");

    private static DateTimeOffset IsoTime(ReadOnlySpan<char> value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new FormatException($"Expected a date, got '{value.ToString()}'.");

    /// <summary>The NUL-separated fields after the commit's own, in order.</summary>
    private sealed class DiffTokens
    {
        private readonly Queue<string> _tokens;

        public DiffTokens(IEnumerable<string> fields)
        {
            // After the format's own NUL, git writes a newline before the first file, and the output ends with a NUL.
            var list = fields.ToList();
            if (list.Count > 0 && list[^1].Length == 0)
                list.RemoveAt(list.Count - 1);
            if (list.Count > 0)
                list[0] = list[0].TrimStart('\n');
            _tokens = new Queue<string>(list);
        }

        /// <summary>Raw entries start with ':' and come before every numstat entry.</summary>
        public bool NextIsRaw() => _tokens.TryPeek(out var token) && token.StartsWith(':');

        public string Take() =>
            _tokens.TryDequeue(out var token) ? token : throw new FormatException("The changed files end too early.");

        public void Skip(int count)
        {
            for (var i = 0; i < count; i++)
                Take();
        }

        public void ExpectEnd()
        {
            if (_tokens.TryPeek(out var extra))
                throw new FormatException($"Unexpected output after the changed files: '{extra}'.");
        }
    }
}
