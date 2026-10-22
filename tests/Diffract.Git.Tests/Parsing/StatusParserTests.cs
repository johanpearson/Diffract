using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Parsing;

public class StatusParserTests
{
    [Fact]
    public void Branch_with_upstream_and_ahead_count()
    {
        var branch = StatusParser.Parse(Sample.Read("status-upstream-ahead.bin")).Branch;

        Assert.Equal("main", branch.Head);
        Assert.Equal("origin/main", branch.Upstream);
        Assert.Equal(3, branch.Ahead);
        Assert.Equal(0, branch.Behind);
        Assert.False(branch.IsDetached);
    }

    [Fact]
    public void Changed_renamed_added_deleted_and_untracked_files()
    {
        var entries = StatusParser.Parse(Sample.Read("status-upstream-ahead.bin")).Entries;

        Assert.Collection(entries,
            e => Assert.Equal((StatusEntryKind.Changed, 'D', '.', "doomed.txt"), (e.Kind, e.IndexStatus, e.WorktreeStatus, e.Path)),
            e =>
            {
                Assert.Equal((StatusEntryKind.RenamedOrCopied, 'R', "renamed åäö.txt"), (e.Kind, e.IndexStatus, e.Path));
                Assert.Equal("to rename.txt", e.OriginalPath);
            },
            e => Assert.Equal((StatusEntryKind.Changed, 'A', "staged.txt"), (e.Kind, e.IndexStatus, e.Path)),
            e => Assert.Equal((StatusEntryKind.Changed, '.', 'M', "tracked.txt"), (e.Kind, e.IndexStatus, e.WorktreeStatus, e.Path)),
            e => Assert.Equal((StatusEntryKind.Untracked, "otäckt.txt"), (e.Kind, e.Path)),
            e => Assert.Equal((StatusEntryKind.Untracked, "untracked file.txt"), (e.Kind, e.Path)));
    }

    [Fact]
    public void Branch_with_no_upstream()
    {
        var report = StatusParser.Parse(Sample.Read("status-no-upstream.bin"));

        Assert.Equal("feature/one", report.Branch.Head);
        Assert.Null(report.Branch.Upstream);
        Assert.Null(report.Branch.Ahead);
        Assert.Null(report.Branch.Behind);
        Assert.Equal("new.txt", Assert.Single(report.Entries).Path);
    }

    [Fact]
    public void Detached_and_clean()
    {
        var report = StatusParser.Parse(Sample.Read("status-detached-clean.bin"));

        Assert.True(report.Branch.IsDetached);
        Assert.NotNull(report.Branch.Oid);
        Assert.Empty(report.Entries);
    }

    [Fact]
    public void Repo_without_commits()
    {
        var report = StatusParser.Parse(Sample.Read("status-initial.bin"));

        Assert.Null(report.Branch.Oid);
        Assert.Equal("main", report.Branch.Head);
        Assert.Equal(StatusEntryKind.Untracked, Assert.Single(report.Entries).Kind);
    }

    [Fact]
    public void Merge_conflict()
    {
        var entry = Assert.Single(StatusParser.Parse(Sample.Read("status-conflict.bin")).Entries);

        Assert.Equal((StatusEntryKind.Unmerged, 'U', 'U', "file.txt"), (entry.Kind, entry.IndexStatus, entry.WorktreeStatus, entry.Path));
    }

    [Fact]
    public void Ignored_file_and_behind_count()
    {
        var report = StatusParser.Parse("# branch.oid abc\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +0 -4\0! bin/out.dll\0");

        Assert.Equal(4, report.Branch.Behind);
        Assert.Equal((StatusEntryKind.Ignored, "bin/out.dll"), (report.Entries[0].Kind, report.Entries[0].Path));
    }

    [Fact]
    public void Unknown_entry_type_is_an_error_not_silently_skipped()
    {
        Assert.Throws<FormatException>(() => StatusParser.Parse("# branch.head main\0x what\0"));
    }

    [Fact]
    public void Blob_ids_of_head_and_index_and_none_for_a_missing_side()
    {
        var entries = StatusParser.Parse(Sample.Read("changes-status.bin")).Entries.ToDictionary(e => e.Path);

        Assert.Equal(("732a4a8caacb0ae2de6aedf0f87b3c94cc3fb8d2", "1331c4ba68d396b070a5151c075a99bc5888cc47"), (entries["staged.txt"].HeadId, entries["staged.txt"].IndexId));
        Assert.Equal((null, "3e757656cf36eca53338e520d134963a44f793f8"), (entries["added.txt"].HeadId, entries["added.txt"].IndexId));
        Assert.Equal(("8da33f1e0bb93c47aff90a14c32b21f1f2c7aa7d", null), (entries["deleted.txt"].HeadId, entries["deleted.txt"].IndexId));
        Assert.Equal(((string?)null, (string?)null), (entries["intent to add.txt"].HeadId, entries["intent to add.txt"].IndexId));
        Assert.Equal("d74a8fa9de478ec483f7c06004e3ac7851bcf642", entries["renamed åäö.txt"].IndexId);
        Assert.Null(entries["untracked file.txt"].HeadId);
    }

    [Fact]
    public void A_submodule_is_marked_and_a_file_is_not()
    {
        var entries = StatusParser.Parse(Sample.Read("changes-status.bin")).Entries.ToDictionary(e => e.Path);

        Assert.True(entries["vendor/library"].IsSubmodule);
        Assert.False(entries["modified.txt"].IsSubmodule);
    }

    [Fact]
    public void Every_untracked_file_on_its_own_and_a_nested_repo_as_one_folder()
    {
        var untracked = StatusParser.Parse(Sample.Read("changes-status.bin")).Entries.Where(e => e.Kind == StatusEntryKind.Untracked);

        Assert.Equal([".claude/worktrees/task/", "new folder/räksmörgås/deep file.txt", "untracked file.txt"], untracked.Select(e => e.Path));
    }

    [Fact]
    public void Conflicted_files_next_to_a_staged_one()
    {
        var entries = StatusParser.Parse(Sample.Read("changes-status-conflict.bin")).Entries;

        Assert.Equal(
            [(StatusEntryKind.Changed, "M.", "clean.txt"), (StatusEntryKind.Unmerged, "AA", "added.txt"), (StatusEntryKind.Unmerged, "UU", "both.txt")],
            entries.Select(e => (e.Kind, $"{e.IndexStatus}{e.WorktreeStatus}", e.Path)));
        Assert.All(entries.Where(e => e.Kind == StatusEntryKind.Unmerged), e => Assert.Null(e.IndexId));
    }

    [Fact]
    public void The_changes_samples_are_captured_with_the_arguments_the_reader_uses()
    {
        var script = Sample.Script("capture-changes-samples.sh");

        Assert.Contains("git " + string.Join(' ', StatusParser.ChangesArguments) + ";", script);
    }
}
