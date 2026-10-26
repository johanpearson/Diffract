using Diffract.Fixtures;
using Diffract.Leftovers.Cleanup;

namespace Diffract.Leftovers.Tests;

public class OperationLogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private OperationLog Log => new(_temp.Combine("oplog"));

    private static CleanupStarted Started(string id, string repo = @"C:\src\repo") =>
        new(id, repo, FixtureRepoBuilder.DefaultNow, null, [Topic]);

    private static readonly LoggedLeftover Topic = new(LeftoverKind.LocalBranch, "topic", null);

    private static StepLogged Step(string id, int step) => new(
        id,
        new StepResult(0, step, StepKind.DeleteBranch, "Delete branch topic", StepOutcome.Done, null),
        new LoggedCommand(["update-ref", "--stdin"], ["start", "create refs/heads/topic abc", "prepare", "commit"]),
        [new Expectation.RefAbsent("refs/heads/topic"), new Expectation.ObjectExists("abc")],
        "Undo works while git still has the commits.");

    [Fact]
    public void A_cleanup_reads_back_as_it_was_written_expectations_included()
    {
        Log.Append(@"C:\src\repo", Started("a"));
        Log.Append(@"C:\src\repo", Step("a", 0));

        var cleanup = Assert.Single(Log.History(@"C:\src\repo"));

        var started = cleanup.Started;
        Assert.Equal(("a", @"C:\src\repo", FixtureRepoBuilder.DefaultNow, (string?)null), (started.CleanupId, started.RepoPath, started.At, started.Undoes));
        Assert.Equal([Topic], started.Leftovers);
        var step = Assert.Single(cleanup.Steps);
        Assert.Equal((Step("a", 0).Result, Step("a", 0).UndoNote), (step.Result, step.UndoNote));
        Assert.Equal(Step("a", 0).Undo!.Input, step.Undo!.Input);
        Assert.Equal(Step("a", 0).UndoExpects, step.UndoExpects);
    }

    [Fact]
    public void Each_repo_has_its_own_file_whatever_case_or_trailing_slash_its_path_is_written_with()
    {
        Assert.Equal(Log.FileFor(@"C:\src\repo"), Log.FileFor(@"c:\SRC\Repo\"));
        Assert.NotEqual(Log.FileFor(@"C:\src\repo"), Log.FileFor(@"C:\src\other"));
        Assert.StartsWith(_temp.Combine("oplog"), Log.FileFor(@"C:\src\repo"));
    }

    [Fact]
    public void Cleanups_of_other_repos_are_not_in_the_history()
    {
        Log.Append(@"C:\src\repo", Started("a"));
        Log.Append(@"C:\src\other", Started("b", @"C:\src\other"));

        Assert.Equal(["a"], Log.History(@"C:\src\repo").Select(c => c.Started.CleanupId));
    }

    [Fact]
    public void A_line_cut_short_by_a_crash_is_left_out_and_the_rest_is_read()
    {
        Log.Append(@"C:\src\repo", Started("a"));
        Log.Append(@"C:\src\repo", Step("a", 0));
        File.AppendAllText(Log.FileFor(@"C:\src\repo"), "{\"type\":\"step\",\"cleanupId\":\"a\",\"res");

        Assert.Single(Assert.Single(Log.History(@"C:\src\repo")).Steps);
    }

    [Fact]
    public void A_repo_never_cleaned_up_has_no_history()
    {
        Assert.Empty(Log.History(@"C:\src\repo"));
    }

    [Fact]
    public void The_log_lives_in_Diffracts_own_folder_never_in_the_repo()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "Diffract", "oplog"), OperationLog.DefaultFolder);
    }

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }
}
