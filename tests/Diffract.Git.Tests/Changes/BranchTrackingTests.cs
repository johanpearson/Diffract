using Diffract.Git.Changes;

namespace Diffract.Git.Tests.Changes;

/// <summary>Against real <c>git for-each-ref</c> output, captured by Samples/capture-changes-samples.sh.</summary>
public sealed class BranchTrackingTests
{
    [Fact]
    public void The_checked_out_branch_and_its_upstream()
    {
        var tracking = BranchTracking.Parse(Sample.Read("tracking-branches.bin"));

        Assert.Equal(new BranchTracking
        {
            Branch = "main",
            Remote = "origin",
            RemoteRef = "refs/heads/main",
            Upstream = "refs/remotes/origin/main",
            UpstreamName = "origin/main",
        }, tracking);
        Assert.True(tracking!.HasRemoteUpstream);
        Assert.True(tracking.SameNameOnRemote);
    }

    [Fact]
    public void A_detached_head_has_no_branch() => Assert.Null(BranchTracking.Parse(Sample.Read("tracking-detached.bin")));

    [Fact]
    public void A_branch_without_upstream_and_one_following_another_name()
    {
        var lines = Sample.Read("tracking-branches.bin").Split('\n');
        var other = BranchTracking.Parse(Checked(lines.Single(l => l.Contains("refs/heads/other\0", StringComparison.Ordinal))))!;
        var none = BranchTracking.Parse(Checked(lines.Single(l => l.Contains("räksmörgås", StringComparison.Ordinal))))!;

        Assert.Equal(("other", true, false), (other.Branch, other.HasRemoteUpstream, other.SameNameOnRemote));
        Assert.Equal(new BranchTracking { Branch = "räksmörgås" }, none);
        Assert.False(none.HasRemoteUpstream);
    }

    [Fact]
    public void A_branch_following_a_local_branch_has_no_remote_upstream()
    {
        var tracking = BranchTracking.Parse("*\0refs/heads/topic\0.\0refs/heads/main\0refs/heads/main\0main\n")!;

        Assert.False(tracking.HasRemoteUpstream);
    }

    [Fact]
    public void The_capture_script_uses_the_same_arguments() =>
        Assert.Contains($"git {string.Join(' ', BranchTracking.Arguments.Select(Quoted))}", Sample.Script("capture-changes-samples.sh"), StringComparison.Ordinal);

    /// <summary>The line as if its branch were the one checked out.</summary>
    private static string Checked(string line) => "*" + line[1..];

    private static string Quoted(string arg) => arg.StartsWith("--format", StringComparison.Ordinal) ? $"\"{arg}\"" : arg;
}
