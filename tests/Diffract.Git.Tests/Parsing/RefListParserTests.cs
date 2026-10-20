using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Parsing;

public class RefListParserTests
{
    private static readonly IReadOnlyList<RefEntry> Refs = RefListParser.Parse(Sample.Read("refs.bin"));

    private static RefEntry Ref(string name) => Assert.Single(Refs, r => r.Name == name);

    [Fact]
    public void Reads_every_ref_in_the_sample()
    {
        Assert.Equal(19, Refs.Count);
        Assert.All(Refs, r => Assert.Equal(40, r.ObjectId.Length));
    }

    [Fact]
    public void Branch_with_no_upstream()
    {
        var branch = Ref("refs/heads/feature/one");

        Assert.Null(branch.Upstream);
        Assert.Equal(UpstreamTracking.None, branch.Tracking);
        Assert.Equal("commit", branch.ObjectType);
    }

    [Fact]
    public void Branch_name_with_swedish_letters()
    {
        Assert.NotNull(Ref("refs/heads/feature/åäö"));
    }

    [Fact]
    public void Branch_in_sync_with_upstream()
    {
        var main = Ref("refs/heads/main");

        Assert.Equal("refs/remotes/origin/main", main.Upstream);
        Assert.Equal(new UpstreamTracking(false, 0, 0), main.Tracking);
    }

    [Fact]
    public void Upstream_gone_after_remote_branch_was_deleted()
    {
        var branch = Ref("refs/heads/pushed-then-gone");

        Assert.Equal("refs/remotes/origin/pushed-then-gone", branch.Upstream);
        Assert.True(branch.Tracking.IsGone);
    }

    [Fact]
    public void Ahead_only_and_ahead_and_behind()
    {
        Assert.Equal(new UpstreamTracking(false, 1, 0), Ref("refs/heads/ahead-behind").Tracking);
        Assert.Equal(new UpstreamTracking(false, 1, 1), Ref("refs/heads/diverged").Tracking);
    }

    [Fact]
    public void Remote_head_is_a_symref()
    {
        Assert.Equal("refs/remotes/origin/main", Ref("refs/remotes/origin/HEAD").SymrefTarget);
        Assert.Null(Ref("refs/remotes/origin/main").SymrefTarget);
    }

    [Fact]
    public void Annotated_tag_is_peeled_to_its_commit()
    {
        var tag = Ref("refs/tags/archive/claude/old-work");

        Assert.Equal("tag", tag.ObjectType);
        Assert.Equal(Ref("refs/heads/main").ObjectId, tag.PeeledObjectId);
        Assert.Equal(tag.PeeledObjectId, tag.TargetId);
    }

    [Fact]
    public void Lightweight_tag_points_straight_at_a_commit()
    {
        var tag = Ref("refs/tags/v1.0");

        Assert.Equal("commit", tag.ObjectType);
        Assert.Null(tag.PeeledObjectId);
        Assert.Equal(tag.ObjectId, tag.TargetId);
    }

    [Fact]
    public void Checkpoint_refs_have_their_commit_dates()
    {
        var checkpoints = Refs.Where(r => r.Name.StartsWith("refs/copilot/checkpoints/7f3c2a9e-session/", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, checkpoints.Count);
        Assert.All(checkpoints, c => Assert.Equal(new DateTimeOffset(2026, 1, 16, 10, 0, 0, TimeSpan.Zero), c.CreatorDate));
    }

    [Fact]
    public void Stash_ref()
    {
        Assert.Equal("commit", Ref("refs/stash").ObjectType);
    }

    [Fact]
    public void Repo_without_refs()
    {
        Assert.Empty(RefListParser.Parse(Sample.Read("refs-empty.bin")));
    }

    [Theory]
    [InlineData("behind 2", false, 0, 2)]
    [InlineData("ahead 12, behind 3", false, 12, 3)]
    [InlineData("gone", true, 0, 0)]
    public void Tracking_text(string track, bool gone, int ahead, int behind)
    {
        var line = $"refs/heads/x\0{new string('a', 40)}\0commit\0\0refs/remotes/origin/x\0{track}\01768471200\0\n";

        Assert.Equal(new UpstreamTracking(gone, ahead, behind), Assert.Single(RefListParser.Parse(line)).Tracking);
    }

    [Fact]
    public void Wrong_field_count_is_an_error()
    {
        Assert.Throws<FormatException>(() => RefListParser.Parse("refs/heads/x\0abc\n"));
    }
}
