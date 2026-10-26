using System.Collections.Concurrent;
using Diffract.Fixtures;

namespace Diffract.Git.Tests;

/// <summary>Git's output as the command window shows it: split into lines as it arrives, progress lines apart.</summary>
public sealed class GitOutputTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static GitOutputLine Line(string text) => new(GitOutputKind.Line, text);

    private static GitOutputLine Progress(string text) => new(GitOutputKind.Progress, text);

    /// <summary>Feeds the pieces one by one, then flushes, and returns every line in order.</summary>
    private static List<GitOutputLine> Split(params string[] pieces)
    {
        var splitter = new GitOutputSplitter();
        var lines = pieces.SelectMany(p => splitter.Feed(p)).ToList();
        lines.AddRange(splitter.Flush());
        return lines;
    }

    [Fact]
    public void Lines_end_at_a_line_feed_and_a_carriage_return_makes_a_progress_line()
    {
        // As git fetch --progress writes it.
        var lines = Split("remote: Enumerating objects: 5, done.\nReceiving objects:  40% (2/5)\rReceiving objects: 100% (5/5), done.\nFrom C:/remote.git\n");

        Assert.Equal([Line("remote: Enumerating objects: 5, done."), Progress("Receiving objects:  40% (2/5)"), Line("Receiving objects: 100% (5/5), done."), Line("From C:/remote.git")], lines);
    }

    [Fact]
    public void A_line_cut_anywhere_comes_out_whole_even_between_CR_and_LF()
    {
        Assert.Equal([Line("räksmörgås"), Progress("50%"), Line("done")], Split("räk", "smörgås\r", "\n50", "%\r", "done"));
    }

    [Fact]
    public void Empty_lines_and_trailing_spaces_are_dropped()
    {
        // Git pads a progress line with spaces so it covers a longer one before it.
        Assert.Equal([Progress("Writing objects:  50%"), Line("done")], Split("\n\r\nWriting objects:  50%   \rdone"));
    }

    [Fact]
    public void A_progress_line_that_git_ends_on_stays_as_the_last_thing_shown()
    {
        Assert.Equal([Line("Writing objects: 100%")], Split("Writing objects: 100%\r"));
    }

    [Fact]
    public void Readable_keeps_only_the_lines_for_an_error_message()
    {
        Assert.Equal("fatal: could not read from remote\nhint: check access", GitOutputSplitter.Readable("Counting: 10%\rCounting: 20%\rfatal: could not read from remote\r\nhint: check access\n"));
    }

    [Fact]
    public void A_command_reads_as_typed_with_spaced_arguments_quoted()
    {
        Assert.Equal(new GitOutputLine(GitOutputKind.Command, "git switch \"has space\""), GitOutputLine.CommandOf(["switch", "has space"]));
    }

    [Fact]
    public async Task A_changes_runner_reports_the_command_then_each_line_while_keeping_all_of_standard_error()
    {
        // Outside a repo, so git refuses at once and changes nothing.
        var seen = new ConcurrentQueue<GitOutputLine>();
        var runner = new GitRunner(_temp.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), ChangesOnly = true, OnOutput = seen.Enqueue });

        var result = await runner.RunAsync(["switch", "main"], [128], TestContext.Current.CancellationToken);

        Assert.Equal([new GitOutputLine(GitOutputKind.Command, "git switch main"), Line("fatal: not a git repository (or any of the parent directories): .git")], seen);
        Assert.StartsWith("fatal: not a git repository", result.StandardError);
    }

    [Fact]
    public async Task A_read_is_not_reported()
    {
        var seen = new ConcurrentQueue<GitOutputLine>();
        var runner = new GitRunner(_temp.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), OnOutput = seen.Enqueue });

        var result = await runner.RunAsync(["rev-parse", "--verify", "no-such-ref"], [128], TestContext.Current.CancellationToken);

        Assert.Empty(seen);
        Assert.StartsWith("fatal: not a git repository", result.StandardError);
    }
}
