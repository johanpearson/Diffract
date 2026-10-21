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
    public void Reads_author_committer_dates_with_their_time_zone_and_the_message()
    {
        var details = Details("files");

        Assert.Equal("Change every kind of file", details.Subject);
        Assert.Equal("The body has two paragraphs.\n\nSecond paragraph, with åäö.", details.Body);
        Assert.Equal(("Sample Author", "author@example.com"), (details.AuthorName, details.AuthorEmail));
        Assert.Equal(("Sample Author", "author@example.com"), (details.CommitterName, details.CommitterEmail));
        Assert.Equal(new DateTimeOffset(2026, 1, 19, 10, 0, 0, TimeSpan.Zero), details.AuthoredAt);
        Assert.Equal(TimeSpan.FromHours(2), Details("octopus").AuthoredAt.Offset);
        Assert.Single(details.ParentIds);
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
        var renamed = File("renamed åäö.txt");

        Assert.Equal("to rename.txt", renamed.OldPath);
        Assert.Equal(85, renamed.Similarity);
        Assert.Equal((1, 0), (renamed.Additions, renamed.Deletions));
        Assert.NotEqual(renamed.OldId, renamed.NewId);
    }

    [Fact]
    public void A_binary_file_has_no_line_counts()
    {
        var binary = File("image.bin");

        Assert.True(binary.IsBinary);
        Assert.Null(binary.Additions);
        Assert.Null(binary.Deletions);
        Assert.False(File("main.txt").IsBinary);
    }

    [Fact]
    public void A_mode_change_keeps_the_same_blob()
    {
        var script = File("script.sh");

        Assert.Equal(script.OldId, script.NewId);
        Assert.Equal((0, 0), (script.Additions, script.Deletions));
        Assert.Null(script.OldPath);
        Assert.Null(script.Similarity);
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
        var submodule = Assert.Single(Details("submodule").Files);

        Assert.Equal("vendor/lib", submodule.Path);
        Assert.True(submodule.IsSubmodule);
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
