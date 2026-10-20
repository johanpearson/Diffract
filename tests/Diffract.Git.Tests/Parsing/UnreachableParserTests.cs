using Diffract.Git.Parsing;

namespace Diffract.Git.Tests.Parsing;

public class UnreachableParserTests
{
    [Fact]
    public void Reads_unreachable_objects_of_every_type()
    {
        var objects = UnreachableParser.Parse(Sample.Read("fsck-unreachable.txt"));

        Assert.Equal(15, objects.Count);
        Assert.Equal(5, objects.Count(o => o.Type == "commit"));
        Assert.Contains(objects, o => o.Type == "tree");
        Assert.Contains(objects, o => o.Type == "blob");
        Assert.All(objects, o => Assert.Equal(40, o.Id.Length));
    }

    [Fact]
    public void Clean_repo_has_none()
    {
        Assert.Empty(UnreachableParser.Parse(Sample.Read("fsck-clean.txt")));
    }

    [Fact]
    public void Ignores_other_fsck_lines_and_crlf()
    {
        var objects = UnreachableParser.Parse("missing blob 1111\r\nunreachable commit abcd\r\nbroken link from tree 2222\r\n");

        Assert.Equal(new UnreachableObject("commit", "abcd"), Assert.Single(objects));
    }
}
