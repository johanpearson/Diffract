using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Parsing;

public class CommitDetailsParserTests
{
    private static CommitDetails Details(string sample) => CommitDetailsParser.Parse(Sample.ReadBytes($"details-{sample}.bin"));

    private static FileChange File(string path) => Details("files").Files.Single(f => f.Path == path);

    [Fact]
    public void The_capture_script_uses_the_parser_format()
    {
        Assert.Contains($"details_format='{CommitDetailsParser.Format}'", Sample.Script("capture-history-samples.sh"));
    }

    [Fact]
    public void Reads_author_committer_dates_and_the_message()
    {
        var d = Details("files");

        Assert.Equal(
            ("Change every kind of file", "The body has two paragraphs.\n\nSecond paragraph, with åäö.",
                "Sample Author", "author@example.com", "Sample Author", "author@example.com",
                new DateTimeOffset(2026, 1, 19, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 19, 10, 0, 0, TimeSpan.Zero),
                "c728b1576c0186c959a65353712b400542ab911f"),
            (d.Subject, d.Body, d.AuthorName, d.AuthorEmail, d.CommitterName, d.CommitterEmail, d.AuthoredAt, d.CommittedAt, string.Join(" ", d.ParentIds)));
    }

    [Fact]
    public void Dates_keep_the_authors_time_zone()
    {
        Assert.Equal(TimeSpan.FromHours(2), Details("octopus").AuthoredAt.Offset);
    }

    [Fact]
    public void Lists_every_kind_of_change_in_gits_order()
    {
        Assert.Equal(
            [
                (FileChangeKind.Deleted, "doomed.txt"),
                (FileChangeKind.Added, "image.bin"),
                (FileChangeKind.Modified, "main.txt"),
                (FileChangeKind.Added, "new file.txt"),
                (FileChangeKind.Renamed, "renamed åäö.txt"),
                (FileChangeKind.Modified, "script.sh"),
            ],
            Details("files").Files.Select(f => (f.Kind, f.Path)));
    }

    [Fact]
    public void An_added_file_has_no_old_side_and_a_deleted_file_no_new_side()
    {
        var added = File("new file.txt");
        Assert.Null(added.OldId);
        Assert.True(ObjectId.IsValid(added.NewId));
        Assert.Equal((1, 0), (added.Additions, added.Deletions));

        var deleted = File("doomed.txt");
        Assert.True(ObjectId.IsValid(deleted.OldId));
        Assert.Null(deleted.NewId);
        Assert.Equal((0, 1), (deleted.Additions, deleted.Deletions));
    }

    [Fact]
    public void A_rename_has_both_paths_its_similarity_and_its_line_counts()
    {
        Assert.Equal(
            new FileChange(FileChangeKind.Renamed, "renamed åäö.txt", "to rename.txt",
                "b2f931a67315c95c5daab3aac6de62e534808476", "b5660615986901aceae1450e10650892e191f8dc",
                Similarity: 85, Additions: 1, Deletions: 0, IsBinary: false, IsSubmodule: false),
            File("renamed åäö.txt"));
    }

    [Fact]
    public void A_binary_file_has_no_line_counts()
    {
        Assert.Equal(
            new FileChange(FileChangeKind.Added, "image.bin", null, null, "0f49c4ae77b43dff338093c78e009676e7e308ba",
                Similarity: null, Additions: null, Deletions: null, IsBinary: true, IsSubmodule: false),
            File("image.bin"));
    }

    [Fact]
    public void A_text_file_is_not_binary()
    {
        Assert.False(File("main.txt").IsBinary);
    }

    [Fact]
    public void A_mode_change_keeps_the_same_blob()
    {
        Assert.Equal(
            new FileChange(FileChangeKind.Modified, "script.sh", null,
                "f5bdd214e01603ecd6c83be9f66d88579c588ec6", "f5bdd214e01603ecd6c83be9f66d88579c588ec6",
                Similarity: null, Additions: 0, Deletions: 0, IsBinary: false, IsSubmodule: false),
            File("script.sh"));
    }

    [Fact]
    public void A_root_commit_adds_its_files()
    {
        var root = Details("root");

        Assert.Empty(root.ParentIds);
        Assert.Equal("", root.Body);
        Assert.Equal([(FileChangeKind.Added, "readme.md")], root.Files.Select(f => (f.Kind, f.Path)));
    }

    [Fact]
    public void A_merge_shows_what_it_changed_against_its_first_parent()
    {
        var merge = Details("merge");

        Assert.Equal(2, merge.ParentIds.Count);
        Assert.Equal(["feature.txt"], merge.Files.Select(f => f.Path));
    }

    [Fact]
    public void An_octopus_merge_shows_what_its_other_parents_brought()
    {
        var octopus = Details("octopus");

        Assert.Equal(3, octopus.ParentIds.Count);
        Assert.Equal(["three.txt", "two.txt"], octopus.Files.Select(f => f.Path));
    }

    [Fact]
    public void A_commit_that_changes_nothing_has_no_files()
    {
        var empty = Details("empty");

        Assert.Equal("Nothing changed", empty.Subject);
        Assert.Empty(empty.Files);
    }

    [Fact]
    public void An_empty_subject_is_an_empty_string()
    {
        var details = Details("empty-subject");

        Assert.Equal("", details.Subject);
        Assert.Equal("Åsa Öberg", details.AuthorName);
        Assert.Equal(["readme.md"], details.Files.Select(f => f.Path));
    }

    [Fact]
    public void A_submodule_is_marked_so_its_content_is_never_asked_for()
    {
        // A submodule's "blob" is a commit in the other repo, and its line counts mean nothing.
        Assert.Equal(
            [new FileChange(FileChangeKind.Added, "vendor/lib", null, null, "2de7b44888e872d49faf7f37e6a5ef62152fa930",
                Similarity: null, Additions: 1, Deletions: 0, IsBinary: false, IsSubmodule: true)],
            Details("submodule").Files);
    }

    [Fact]
    public void An_ordinary_file_is_not_a_submodule()
    {
        Assert.False(File("main.txt").IsSubmodule);
    }

    [Fact]
    public void Truncated_output_is_an_error()
    {
        var bytes = Sample.ReadBytes("details-files.bin");

        Assert.Throws<FormatException>(() => CommitDetailsParser.Parse(bytes[..(bytes.Length - 20)]));
        Assert.Throws<FormatException>(() => CommitDetailsParser.Parse(bytes[..50]));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("--output=x")]
    [InlineData("")]
    public void Arguments_refuse_anything_but_an_object_id(string commit)
    {
        Assert.Throws<ArgumentException>(() => CommitDetailsParser.Arguments(commit));
    }
}
