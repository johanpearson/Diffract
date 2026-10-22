using System.Text;
using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Parsing;

public class CommitLogParserTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<CommitEntry> History => CommitLogParser.Parse(Sample.ReadBytes("log-history.bin"));

    private static CommitEntry BySubject(string subject) => History.Single(c => c.Subject == subject);

    [Fact]
    public void The_capture_script_uses_the_parser_format()
    {
        Assert.Contains($"log_format='{CommitLogParser.Format}'", Sample.Script("capture-history-samples.sh"));
    }

    [Fact]
    public void Reads_every_commit_in_topological_order()
    {
        Assert.Equal(17, History.Count);
        Assert.Equal(History.Count, History.Select(c => c.Id).Distinct().Count());

        // Topological: every commit comes before all of its parents.
        var position = History.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        Assert.All(History, c => Assert.All(c.ParentIds, p => Assert.True(position[p] > position[c.Id])));
    }

    [Fact]
    public void Root_commits_have_no_parents()
    {
        Assert.Equal(["Initial commit", "Second root"], History.Where(c => c.ParentIds.Count == 0).Select(c => c.Subject).Order());
    }

    [Fact]
    public void Merges_list_every_parent_first_parent_first()
    {
        var merge = BySubject("Merge branch 'feature'");
        Assert.Equal([BySubject("Work on main").Id, BySubject("Add feature").Id], merge.ParentIds);

        var octopus = BySubject("Octopus merge");
        Assert.Equal(["Octopus side one", "Octopus side two", "Octopus side three"], octopus.ParentIds.Select(p => History.Single(c => c.Id == p).Subject));
    }

    [Fact]
    public void Reads_names_and_subjects_beyond_ascii()
    {
        var commit = BySubject("Lägg till räksmörgås 🦐");
        Assert.Equal("Åsa Öberg", commit.AuthorName);
        Assert.Equal("åsa@exempel.se", commit.AuthorEmail);
    }

    [Fact]
    public void Git_turns_a_latin1_message_into_utf8()
    {
        Assert.Single(History, c => c.Subject == "Latin-1 åäö");
    }

    [Fact]
    public void An_empty_subject_is_an_empty_string()
    {
        var commit = Assert.Single(History, c => c.Subject.Length == 0);
        Assert.Equal("Åsa Öberg", commit.AuthorName);
    }

    [Fact]
    public void Reads_author_and_commit_times()
    {
        var commit = BySubject("Initial commit");
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero), commit.AuthoredAt);
        Assert.Equal(commit.AuthoredAt, commit.CommittedAt);

        // Unix time keeps the moment, not the author's time zone, which only the details show.
        Assert.Equal(new DateTimeOffset(2026, 1, 18, 15, 0, 0, TimeSpan.Zero), BySubject("Lägg till räksmörgås 🦐").AuthoredAt);
    }

    [Fact]
    public void A_detached_head_passed_as_a_head_is_read()
    {
        Assert.Single(History, c => c.Subject == "Only on a detached HEAD");
    }

    [Fact]
    public void An_empty_repo_has_no_history()
    {
        Assert.Empty(CommitLogParser.Parse(Sample.ReadBytes("log-empty.bin")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(500)]
    public async Task Pages_hold_page_size_commits_and_the_last_one_the_rest(int pageSize)
    {
        List<IReadOnlyList<CommitEntry>> pages = [];

        var total = await CommitLogParser.ReadPagesAsync(new MemoryStream(Sample.ReadBytes("log-history.bin")), pageSize, Collect(pages), Ct);

        Assert.Equal(17, total);
        Assert.Equal((17 + pageSize - 1) / pageSize, pages.Count);
        Assert.All(pages.SkipLast(1), p => Assert.Equal(pageSize, p.Count));
        Assert.Equal(History.Select(Describe), pages.SelectMany(p => p).Select(Describe));
    }

    [Fact]
    public async Task Gives_the_same_result_however_the_stream_is_cut()
    {
        // One byte per read splits every field, and every multi-byte character, across reads.
        List<IReadOnlyList<CommitEntry>> pages = [];

        await CommitLogParser.ReadPagesAsync(new TrickleStream(Sample.ReadBytes("log-history.bin")), 4, Collect(pages), Ct);

        Assert.Equal(History.Select(Describe), pages.SelectMany(p => p).Select(Describe));
    }

    [Fact]
    public async Task The_first_page_is_handed_on_before_the_stream_ends()
    {
        var bytes = Sample.ReadBytes("log-history.bin");
        var stream = new GatedStream(bytes, firstPart: bytes.Length / 2);
        var firstPage = new TaskCompletionSource();

        var reading = CommitLogParser.ReadPagesAsync(stream, 2, _ =>
        {
            firstPage.TrySetResult();
            return Task.CompletedTask;
        }, Ct);

        await firstPage.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(reading.IsCompleted);
        stream.Open();
        Assert.Equal(17, await reading);
    }

    [Fact]
    public void A_cut_off_commit_is_an_error_not_a_shorter_history()
    {
        var bytes = Sample.ReadBytes("log-history.bin");
        var cut = bytes.AsSpan(0, Array.IndexOf(bytes, (byte)0, 60) + 1).ToArray();

        Assert.Throws<FormatException>(() => CommitLogParser.Parse(cut));
    }

    [Fact]
    public void Arguments_read_the_commits_from_standard_input_only()
    {
        Assert.Equal(["log", "--topo-order", "-z", "--no-show-signature", "--format=" + CommitLogParser.Format, "--stdin", "--"], CommitLogParser.Arguments);
    }

    [Fact]
    public void Input_names_each_tip_once_and_each_excluded_commit_once_with_a_caret()
    {
        var (a, b, c) = (new string('a', 40), new string('b', 40), new string('c', 64));

        var input = CommitLogParser.Input([a, b, a], [c, c]);

        Assert.Equal([a, b, "^" + c], input);
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("HEAD")]
    [InlineData("main~1")]
    [InlineData("^aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Input_refuses_anything_but_an_object_id(string tip)
    {
        Assert.Throws<ArgumentException>(() => CommitLogParser.Input([tip]));
        Assert.Throws<ArgumentException>(() => CommitLogParser.Input([], [tip]));
    }

    [Fact]
    public async Task Reading_at_most_a_number_of_commits_gives_all_of_them_or_null()
    {
        var bytes = Sample.ReadBytes("log-history.bin");
        var count = CommitLogParser.Parse(bytes).Count;

        var all = await CommitLogParser.ReadAtMostAsync(new MemoryStream(bytes), count, Ct);
        var tooMany = await CommitLogParser.ReadAtMostAsync(new MemoryStream(bytes), count - 1, Ct);

        Assert.Equal(count, all?.Count);
        Assert.Null(tooMany);
    }

    /// <summary>Every field, parents included, which record equality compares only by reference.</summary>
    private static string Describe(CommitEntry c) =>
        $"{c.Id} [{string.Join(' ', c.ParentIds)}] {c.AuthorName} <{c.AuthorEmail}> {c.AuthoredAt:O} {c.CommittedAt:O} {c.Subject}";

    private static Func<IReadOnlyList<CommitEntry>, Task> Collect(List<IReadOnlyList<CommitEntry>> pages) => page =>
    {
        pages.Add(page);
        return Task.CompletedTask;
    };

    [Fact]
    public async Task Field_reader_grows_for_a_field_larger_than_its_buffer()
    {
        var big = new string('å', 1000);
        var reader = new NulFieldReader(new TrickleStream(Encoding.UTF8.GetBytes(big + "\0tail")), bufferSize: 16);

        Assert.Equal(big, await reader.ReadAsync(Ct));
        Assert.Equal("tail", await reader.ReadAsync(Ct));
        Assert.Null(await reader.ReadAsync(Ct));
    }
}

/// <summary>Hands out one byte per read.</summary>
internal sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
{
    public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}

/// <summary>Hands out the first part at once, then waits for <see cref="Open"/> before the rest, like git still working.</summary>
internal sealed class GatedStream(byte[] bytes, int firstPart) : MemoryStream(bytes)
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _open.TrySetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= firstPart)
            await _open.Task.WaitAsync(cancellationToken);
        var limit = Position < firstPart ? (int)Math.Min(buffer.Length, firstPart - Position) : buffer.Length;
        return await base.ReadAsync(buffer[..limit], cancellationToken);
    }
}
