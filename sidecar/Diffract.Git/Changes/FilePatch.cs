using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Diffract.Git.Changes;

/// <summary>One hunk of a file's diff, as git makes it with no context lines.</summary>
public sealed record Hunk
{
    /// <summary>
    /// Made from the hunk's bytes and its file's header, which names both blobs. Any change to the file gives every
    /// hunk a new id, so a hunk can only be staged exactly as the user saw it.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The first line the hunk removes, or the line it adds after when it removes none. 1-based.</summary>
    public required int OldStart { get; init; }

    public required int OldLines { get; init; }

    public required int NewStart { get; init; }

    public required int NewLines { get; init; }
}

/// <summary>Which diff of a file to read.</summary>
/// <param name="OldPath">The path before a staged rename, so its two paths are read as one diff. Null otherwise.</param>
/// <param name="Staged">HEAD against the index when true, the index against the worktree when false.</param>
public sealed record DiffTarget(string Path, string? OldPath, bool Staged);

/// <summary>
/// A diff from <c>git diff -U0</c>, kept as bytes: a patch cut as text loses the CRs of a CRLF file, and git then
/// refuses it. Every byte stays as git wrote it, except in the header of a rename or copy (see <see cref="PatchHeader"/>).
/// </summary>
public sealed partial class FilePatch
{
    private readonly List<(Hunk Hunk, byte[] Patch)> _hunks = [];

    private FilePatch()
    {
    }

    public IReadOnlyList<Hunk> Hunks => [.. _hunks.Select(h => h.Hunk)];

    /// <summary>
    /// The diff of one file, or of a rename's two paths, without context lines, so changes a few lines apart are
    /// hunks of their own. Fixed prefixes and no colour, external diff or textconv, whatever the user's config says,
    /// so <c>git apply</c> takes the patch back. Paths are literal, never patterns.
    /// </summary>
    public static IReadOnlyList<string> Arguments(DiffTarget target)
    {
        List<string> args = ["diff", "-U0", "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/"];
        if (target.Staged)
            args.AddRange(["--cached", "-M"]);
        args.AddRange(["--", ":(literal)" + target.Path]);
        if (target.Staged && target.OldPath is not null)
            args.Add(":(literal)" + target.OldPath);
        return args;
    }

    public static FilePatch Parse(byte[] diff)
    {
        var patch = new FilePatch();
        foreach (var section in Sections(PatchLine.Split(diff)))
            patch.Add(section);
        return patch;
    }

    /// <summary>The patch that applies just this hunk: its file's header and the hunk. Null when no hunk has the id.</summary>
    public byte[]? PatchFor(string hunkId) => _hunks.FirstOrDefault(h => h.Hunk.Id == hunkId).Patch;

    /// <summary>Each file in the diff: its header lines, then its hunks, each starting at an "@@" line.</summary>
    private static List<Section> Sections(IEnumerable<PatchLine> lines)
    {
        List<Section> sections = [];
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git "u8) || sections.Count == 0)
                sections.Add(new Section());
            sections[^1].Add(line);
        }
        return sections;
    }

    private void Add(Section section)
    {
        var header = PatchHeader.AsChangeOfNewPath(section.Header);
        foreach (var hunk in section.Hunks)
        {
            var patch = PatchLine.Join([.. header, .. hunk]);
            _hunks.Add((Describe(hunk[0], patch), patch));
        }
    }

    private static Hunk Describe(PatchLine headerLine, byte[] patch)
    {
        var match = HunkHeader().Match(headerLine.Text);
        if (!match.Success)
            throw new FormatException($"Not a hunk header: '{headerLine.Text}'");
        return new Hunk
        {
            Id = Convert.ToHexStringLower(SHA256.HashData(patch))[..32],
            OldStart = Number(match.Groups["oldStart"]),
            OldLines = Count(match.Groups["oldLines"]),
            NewStart = Number(match.Groups["newStart"]),
            NewLines = Count(match.Groups["newLines"]),
        };
    }

    private static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    /// <summary>A count left out means one line.</summary>
    private static int Count(Group group) => group.Success ? Number(group) : 1;

    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(,(?<oldLines>\d+))? \+(?<newStart>\d+)(,(?<newLines>\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <summary>One file of a diff, read line by line.</summary>
    private sealed class Section
    {
        public List<PatchLine> Header { get; } = [];

        public List<List<PatchLine>> Hunks { get; } = [];

        public void Add(PatchLine line)
        {
            if (line.StartsWith("@@ "u8))
                Hunks.Add([]);
            (Hunks.Count == 0 ? Header : Hunks[^1]).Add(line);
        }
    }
}
