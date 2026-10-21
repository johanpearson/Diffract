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
    /// The refs history shows by default: local branches, remote-tracking branches, tags, and the HEAD of every
    /// worktree, passed as <paramref name="heads"/>. A detached HEAD is the only one not already among the branches.
    /// </summary>
    public static IReadOnlyList<string> Arguments(IEnumerable<string> heads)
    {
        // --no-show-signature: log.showSignature would mix gpg's output into the stream.
        List<string> args = ["log", "--topo-order", "-z", "--no-show-signature", "--format=" + Format, "--branches", "--remotes", "--tags"];
        args.AddRange(heads.Select(ObjectId.Require).Distinct(StringComparer.Ordinal));
        args.Add("--");
        return args;
    }

    /// <summary>Reads commits as they stream and hands them on in pages of <paramref name="pageSize"/>, the last one shorter.</summary>
    /// <returns>How many commits were read.</returns>
    public static async Task<int> ReadPagesAsync(Stream output, int pageSize, Func<IReadOnlyList<CommitEntry>, Task> onPage, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var reader = new NulFieldReader(output);
        var fields = new string[FieldCount];
        var page = new List<CommitEntry>(Math.Min(pageSize, PageCapacity));
        var total = 0;

        while (await reader.ReadAsync(ct).ConfigureAwait(false) is { } first)
        {
            // A stray newline between records is all git ever adds. Nothing at all ends the stream.
            if (first.Length == 0 || first == "\n")
                continue;

            fields[0] = first;
            for (var i = 1; i < FieldCount; i++)
            {
                fields[i] = await reader.ReadAsync(ct).ConfigureAwait(false)
                    ?? throw new FormatException($"The history ended in the middle of commit {first}.");
            }

            page.Add(Parse(fields));
            total++;
            if (page.Count == pageSize)
            {
                await onPage(page).ConfigureAwait(false);
                page = new List<CommitEntry>(Math.Min(pageSize, PageCapacity));
            }
        }

        if (page.Count > 0)
            await onPage(page).ConfigureAwait(false);
        return total;
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
