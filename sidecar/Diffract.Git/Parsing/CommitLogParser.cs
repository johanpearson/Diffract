using System.Globalization;

namespace Diffract.Git.Parsing;

/// <summary>One commit as the history list shows it.</summary>
/// <param name="ParentIds">First parent first. Empty for a root commit.</param>
/// <param name="Subject">The first line of the message. Can be empty.</param>
public sealed record CommitEntry(
    string Id,
    IReadOnlyList<string> ParentIds,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    DateTimeOffset CommittedAt,
    string Subject);

/// <summary>
/// Parses <c>git log -z</c> with <see cref="Format"/>: seven NUL-terminated fields per commit. Names, emails and
/// subjects cannot hold a NUL, so the fields never need escaping.
/// </summary>
public static class CommitLogParser
{
    private const int FieldCount = 7;
    private const int PageCapacity = 1000;

    public const string Format = "%H%x00%P%x00%an%x00%ae%x00%at%x00%ct%x00%s";

    /// <summary>
    /// The commits to read come on standard input, one per line: <see cref="Input"/>. A repo can have more refs than
    /// fit on a command line.
    /// </summary>
    /// <remarks>--no-show-signature: log.showSignature would mix gpg's output into the stream.</remarks>
    public static IReadOnlyList<string> Arguments { get; } =
        ["log", "--topo-order", "-z", "--no-show-signature", "--format=" + Format, "--stdin", "--"];

    /// <summary>
    /// Standard input for <see cref="Arguments"/>: the commits reachable from <paramref name="tips"/> and from none of
    /// <paramref name="excluded"/>. Each id once.
    /// </summary>
    public static IReadOnlyList<string> Input(IEnumerable<string> tips, IEnumerable<string>? excluded = null) =>
    [
        .. tips.Select(ObjectId.Require).Distinct(StringComparer.Ordinal),
        .. (excluded ?? []).Select(ObjectId.Require).Distinct(StringComparer.Ordinal).Select(id => "^" + id),
    ];

    /// <summary>All the commits in the stream, or null as soon as there are more than <paramref name="max"/>.</summary>
    public static async Task<IReadOnlyList<CommitEntry>?> ReadAtMostAsync(Stream output, int max, CancellationToken ct)
    {
        var reader = new NulFieldReader(output);
        List<CommitEntry> commits = [];
        while (await ReadCommitAsync(reader, ct).ConfigureAwait(false) is { } commit)
        {
            if (commits.Count == max)
                return null;
            commits.Add(commit);
        }
        return commits;
    }

    /// <summary>Reads commits as they stream and hands them on in pages of <paramref name="pageSize"/>, the last one shorter.</summary>
    /// <returns>How many commits were read.</returns>
    public static async Task<int> ReadPagesAsync(Stream output, int pageSize, Func<IReadOnlyList<CommitEntry>, Task> onPage, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var reader = new NulFieldReader(output);
        var pages = new Pager(pageSize, onPage);
        while (await ReadCommitAsync(reader, ct).ConfigureAwait(false) is { } commit)
            await pages.AddAsync(commit).ConfigureAwait(false);
        return await pages.FinishAsync().ConfigureAwait(false);
    }

    /// <summary>The next commit, or null at the end of the stream.</summary>
    private static async ValueTask<CommitEntry?> ReadCommitAsync(NulFieldReader reader, CancellationToken ct)
    {
        if (await RecordStartAsync(reader, ct).ConfigureAwait(false) is not { } first)
            return null;

        var fields = new string[FieldCount];
        fields[0] = first;
        for (var i = 1; i < FieldCount; i++)
        {
            fields[i] = await reader.ReadAsync(ct).ConfigureAwait(false)
                ?? throw new FormatException($"The history ended in the middle of commit {first}.");
        }
        return Parse(fields);
    }

    /// <summary>The first field of the next commit. A stray newline between records is all git ever adds.</summary>
    private static async ValueTask<string?> RecordStartAsync(NulFieldReader reader, CancellationToken ct)
    {
        string? field;
        do
        {
            field = await reader.ReadAsync(ct).ConfigureAwait(false);
        }
        while (field is "" or "\n");
        return field;
    }

    /// <summary>Collects commits and hands each full page on, then whatever is left at the end.</summary>
    private sealed class Pager(int pageSize, Func<IReadOnlyList<CommitEntry>, Task> onPage)
    {
        private List<CommitEntry> _page = NewPage(pageSize);
        private int _total;

        public async Task AddAsync(CommitEntry commit)
        {
            _page.Add(commit);
            _total++;
            if (_page.Count == pageSize)
                await FlushAsync().ConfigureAwait(false);
        }

        /// <returns>How many commits there were.</returns>
        public async Task<int> FinishAsync()
        {
            if (_page.Count > 0)
                await FlushAsync().ConfigureAwait(false);
            return _total;
        }

        private async Task FlushAsync()
        {
            var full = _page;
            _page = NewPage(pageSize);
            await onPage(full).ConfigureAwait(false);
        }

        private static List<CommitEntry> NewPage(int pageSize) => new(Math.Min(pageSize, PageCapacity));
    }

    /// <summary>Parses complete output, such as a captured sample.</summary>
    public static IReadOnlyList<CommitEntry> Parse(byte[] output)
    {
        List<CommitEntry> commits = [];
        ReadPagesAsync(new MemoryStream(output), int.MaxValue, page =>
        {
            commits.AddRange(page);
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        return commits;
    }

    private static CommitEntry Parse(string[] f) => new(
        Id: ObjectId.Parse(f[0]),
        ParentIds: f[1].Length == 0 ? [] : f[1].Split(' ').Select(ObjectId.Parse).ToArray(),
        AuthorName: f[2],
        AuthorEmail: f[3],
        AuthoredAt: UnixTime(f[4]),
        CommittedAt: UnixTime(f[5]),
        Subject: f[6]);

    private static DateTimeOffset UnixTime(string seconds) =>
        long.TryParse(seconds, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : throw new FormatException($"Expected a unix time, got '{seconds}'.");
}
