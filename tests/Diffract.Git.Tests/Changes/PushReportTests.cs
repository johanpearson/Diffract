using Diffract.Git.Changes;

namespace Diffract.Git.Tests.Changes;

/// <summary>Against real <c>git push --porcelain</c> output, captured by Samples/capture-changes-samples.sh.</summary>
public sealed class PushReportTests
{
    [Theory]
    [InlineData("push-new-branch.txt", PushedRefState.New, "[new branch]")]
    [InlineData("push-fast-forward.txt", PushedRefState.FastForward, "e46c46e..26d2bca")]
    [InlineData("push-up-to-date.txt", PushedRefState.UpToDate, "[up to date]")]
    [InlineData("push-rejected.txt", PushedRefState.Rejected, "[rejected] (fetch first)")]
    public void Reads_the_one_ref_line_of_each_kind_of_push(string sample, PushedRefState state, string summary)
    {
        var pushed = Assert.Single(PushReport.Parse(Sample.Read(sample)));

        Assert.Equal(new PushedRef(state, "refs/heads/main", summary), pushed);
    }

    [Fact]
    public void A_line_set_up_to_track_and_the_to_and_done_lines_are_not_refs()
    {
        Assert.Contains("set up to track", Sample.Read("push-new-branch.txt"), StringComparison.Ordinal);

        Assert.Empty(PushReport.Parse("To C:/remote.git\nbranch 'main' set up to track 'origin/main'.\nDone\n"));
    }

    [Theory]
    [InlineData("+", PushedRefState.Other)]
    [InlineData("-", PushedRefState.Other)]
    [InlineData("!", PushedRefState.Rejected)]
    public void Flags_diffract_never_asks_for_are_other(string flag, PushedRefState state)
    {
        var pushed = Assert.Single(PushReport.Parse($"To x\n{flag}\trefs/heads/a:refs/heads/b\tsummary\r\nDone\n"));

        Assert.Equal(new PushedRef(state, "refs/heads/b", "summary"), pushed);
    }

    [Fact]
    public void Nothing_written_means_no_refs() => Assert.Empty(PushReport.Parse(""));
}
