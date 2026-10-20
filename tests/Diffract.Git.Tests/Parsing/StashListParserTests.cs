using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Parsing;

public class StashListParserTests
{
    [Fact]
    public void Reads_stashes_newest_first()
    {
        var stashes = StashListParser.Parse(Sample.Read("stash-list.bin"));

        Assert.Equal([0, 1, 2], stashes.Select(s => s.Index));
        Assert.Equal(["stash@{0}", "stash@{1}", "stash@{2}"], stashes.Select(s => s.Name));
        Assert.All(stashes, s => Assert.Equal(40, s.CommitId.Length));
    }

    [Fact]
    public void Messages_for_default_named_and_tool_stashes()
    {
        var stashes = StashListParser.Parse(Sample.Read("stash-list.bin"));

        Assert.StartsWith("WIP on main: ", stashes[0].Message);
        Assert.Equal("On main: Fix för åäö", stashes[1].Message);
        Assert.Equal("On main: autostash", stashes[2].Message);
    }

    [Fact]
    public void Creation_time()
    {
        var autostash = StashListParser.Parse(Sample.Read("stash-list.bin"))[2];

        Assert.Equal(new DateTimeOffset(2026, 1, 16, 10, 0, 0, TimeSpan.Zero), autostash.CreatedAt);
    }

    [Fact]
    public void No_stashes()
    {
        Assert.Empty(StashListParser.Parse(Sample.Read("stash-list-empty.bin")));
    }
}
