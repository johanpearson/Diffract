using System.Text;
using Diffract.Git.Parsing;
using Diffract.Git.Snapshot;

namespace Diffract.Git.History;

public enum FileContentKind { Text, Binary, TooLarge }

/// <summary>One side of a file's diff.</summary>
/// <param name="Text">The content when <see cref="Kind"/> is Text, decoded as UTF-8 without a byte order mark. Null otherwise.</param>
public sealed record FileContent(FileContentKind Kind, string? Text);

/// <summary>
/// Reads a repo's history with as few git calls as possible: one streamed call for the whole list, one for a
/// commit's details, and one for each side of a file's diff.
/// </summary>
public sealed class HistoryReader
{
    /// <summary>Commits per page sent to the UI. The first page goes out as soon as it is parsed.</summary>
    public const int PageSize = 500;

    /// <summary>Larger files are refused rather than sent whole.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>As git: a file is binary when its first 8000 bytes hold a NUL.</summary>
    private const int BinaryCheckBytes = 8000;

    private readonly GitRunnerOptions _runnerOptions;
    private readonly int _pageSize;

    /// <param name="pageSize">Commits per page that <see cref="ReadAsync"/> hands on. Tests use small pages.</param>
    public HistoryReader(GitRunnerOptions runnerOptions, int pageSize = PageSize)
    {
        if (!runnerOptions.ReadOnly)
            throw new ArgumentException("The history reader only runs with a read-only git runner.", nameof(runnerOptions));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _runnerOptions = runnerOptions;
        _pageSize = pageSize;
    }

    /// <summary>The worktree HEADs history starts from, besides branches and tags. Unborn branches have none.</summary>
    public static IReadOnlyList<string> HeadsOf(RepoSnapshot snapshot) =>
        snapshot.Worktrees.Select(w => w.Head).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Reads the whole history in topological order with one git call, handing it on page by page as it streams.</summary>
    /// <returns>How many commits were read.</returns>
    public async Task<int> ReadAsync(string repoRoot, IEnumerable<string> heads, Func<IReadOnlyList<CommitEntry>, Task> onPage, CancellationToken ct)
    {
        var total = 0;
        await Runner(repoRoot).StreamAsync(CommitLogParser.Arguments(heads), async (output, token) =>
        {
            total = await CommitLogParser.ReadPagesAsync(output, _pageSize, onPage, token).ConfigureAwait(false);
            return true;
        }, [], ct).ConfigureAwait(false);
        return total;
    }

    public async Task<CommitDetails> ReadDetailsAsync(string repoRoot, string commitId, CancellationToken ct)
    {
        var result = await Runner(repoRoot).RunAsync(CommitDetailsParser.Arguments(commitId), ct).ConfigureAwait(false);
        return CommitDetailsParser.Parse(result.StandardOutputBytes);
    }

    /// <summary>
    /// Reads one blob, stopping git as soon as it is clearly binary or larger than <paramref name="maxBytes"/>.
    /// <c>cat-file</c> hands over the stored bytes, so no filter or textconv from the repo's config ever runs.
    /// </summary>
    public async Task<FileContent> ReadFileAsync(string repoRoot, string blobId, CancellationToken ct, int maxBytes = MaxFileBytes)
    {
        using var buffer = new MemoryStream();
        FileContentKind? stoppedAs = null;
        await Runner(repoRoot).StreamAsync(["cat-file", "blob", ObjectId.Require(blobId)], async (output, token) =>
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await output.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                var checkedBefore = (int)Math.Min(buffer.Length, BinaryCheckBytes);
                buffer.Write(chunk, 0, read);
                if (checkedBefore < BinaryCheckBytes && HasNul(buffer, checkedBefore))
                {
                    stoppedAs = FileContentKind.Binary;
                    return false;
                }
                if (buffer.Length > maxBytes)
                {
                    stoppedAs = FileContentKind.TooLarge;
                    return false;
                }
            }
            return true;
        }, [], ct).ConfigureAwait(false);

        return stoppedAs is { } kind
            ? new FileContent(kind, null)
            : new FileContent(FileContentKind.Text, Decode(buffer));
    }

    private static bool HasNul(MemoryStream buffer, int from)
    {
        var end = (int)Math.Min(buffer.Length, BinaryCheckBytes);
        return buffer.GetBuffer().AsSpan(from, end - from).Contains((byte)0);
    }

    private static string Decode(MemoryStream buffer)
    {
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        return Encoding.UTF8.GetString(bytes);
    }

    private GitRunner Runner(string repoRoot) => new(repoRoot, _runnerOptions);
}
