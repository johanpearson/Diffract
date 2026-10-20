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
}
