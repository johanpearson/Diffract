using Diffract.Git.Changes;
using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Changes;

/// <summary>What the Changes view lists, from captured status output. See Samples/capture-changes-samples.sh.</summary>
public class WorktreeChangesTests
{
    private const string Worktree = "C:/src/shop";

    private static WorktreeChanges Read(string sample) => WorktreeChanges.From(Worktree, StatusParser.Parse(Sample.Read(sample)));

    private static Dictionary<string, ChangedFile> Files(string sample) => Read(sample).Files.ToDictionary(f => f.Path);

    [Fact]
    public void Branch_upstream_and_ahead_and_behind()
    {
        var changes = Read("changes-status.bin");

        Assert.Equal((Worktree, "main", "origin/main", 1, 2), (changes.Worktree, changes.Branch, changes.Upstream, changes.Ahead, changes.Behind));
        Assert.Equal("6726bf1bd92176f0b5358a2e1b6410be623fa2bf", changes.HeadCommit);
    }

    [Theory]
    [InlineData("modified.txt", null, FileChangeKind.Modified)]
    [InlineData("staged.txt", FileChangeKind.Modified, null)]
    [InlineData("both.txt", FileChangeKind.Modified, FileChangeKind.Modified)]
    [InlineData("added.txt", FileChangeKind.Added, null)]
    [InlineData("deleted.txt", FileChangeKind.Deleted, null)]
    [InlineData("removed.txt", null, FileChangeKind.Deleted)]
    [InlineData("renamed åäö.txt", FileChangeKind.Renamed, null)]
    [InlineData("renamed then edited.txt", FileChangeKind.Renamed, FileChangeKind.Modified)]
    [InlineData("intent to add.txt", null, FileChangeKind.Added)]
    [InlineData("untracked file.txt", null, FileChangeKind.Added)]
    [InlineData("new folder/räksmörgås/deep file.txt", null, FileChangeKind.Added)]
    public void Each_file_is_staged_unstaged_or_both(string path, FileChangeKind? staged, FileChangeKind? unstaged)
    {
        var file = Files("changes-status.bin")[path];

        Assert.Equal((staged, unstaged), (file.Staged, file.Unstaged));
    }

    [Fact]
    public void A_rename_keeps_its_old_path_and_both_blobs()
    {
        var file = Files("changes-status.bin")["renamed åäö.txt"];

        Assert.Equal(("to rename.txt", ChangedFileKind.Tracked), (file.OldPath, file.Kind));
        Assert.Equal("d74a8fa9de478ec483f7c06004e3ac7851bcf642", file.HeadId);
        Assert.Equal(file.HeadId, file.IndexId);
    }

    [Fact]
    public void Untracked_files_and_an_intent_to_add_file_have_no_blobs()
    {
        var files = Files("changes-status.bin");

        Assert.Equal(ChangedFileKind.Untracked, files["untracked file.txt"].Kind);
        Assert.Equal(ChangedFileKind.Tracked, files["intent to add.txt"].Kind);
        Assert.All([files["untracked file.txt"], files["intent to add.txt"]], f => Assert.Equal((null, null), (f.HeadId, f.IndexId)));
    }

    [Fact]
    public void An_agent_worktree_inside_the_repo_is_a_nested_repo_without_its_trailing_slash()
    {
        var file = Files("changes-status.bin")[".claude/worktrees/task"];

        Assert.Equal((ChangedFileKind.NestedRepo, null, FileChangeKind.Added), (file.Kind, file.Staged, file.Unstaged));
    }

    [Fact]
    public void A_submodule_has_no_blobs_to_read()
    {
        var file = Files("changes-status.bin")["vendor/library"];

        Assert.Equal((ChangedFileKind.Submodule, FileChangeKind.Modified), (file.Kind, file.Unstaged));
        Assert.Equal((null, null), (file.HeadId, file.IndexId));
    }

    [Fact]
    public void Conflicted_files_are_listed_as_conflicted_without_a_diff()
    {
        var files = Files("changes-status-conflict.bin");

        Assert.All([files["both.txt"], files["added.txt"]], f =>
            Assert.Equal((ChangedFileKind.Conflicted, null, null, null), (f.Kind, f.Staged, f.Unstaged, f.IndexId)));
        Assert.Equal((ChangedFileKind.Tracked, FileChangeKind.Modified), (files["clean.txt"].Kind, files["clean.txt"].Staged));
    }

    [Fact]
    public void Ignored_files_are_left_out()
    {
        var changes = WorktreeChanges.From(Worktree, StatusParser.Parse("# branch.oid (initial)\0# branch.head main\0! bin/out.dll\0? new.txt\0"));

        Assert.Equal("new.txt", Assert.Single(changes.Files).Path);
        Assert.Null(changes.HeadCommit);
    }
}
