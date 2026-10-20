namespace Diffract.Git.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("claude/*", "claude/fix-login")]
    [InlineData("copilot/*", "copilot/update-deps")]
    [InlineData("refs/copilot/checkpoints/**", "refs/copilot/checkpoints/7f3c/1/cp-1")]
    [InlineData("**/copilot-worktrees/*", "C:/Users/me/repos/copilot-worktrees/shop")]
    [InlineData("**/copilot-worktrees/*", @"C:\Users\me\repos\copilot-worktrees\shop")]
    [InlineData("**/copilot-worktrees/**", "C:/repos/copilot-worktrees/shop/me-curly-waddle")]
    [InlineData("**/copilot-worktrees/*/copilot-prewarm-*", "C:/repos/copilot-worktrees/shop/copilot-prewarm-quiet-fox")]
    [InlineData("**/.claude/worktrees", "C:/src/shop/.claude/worktrees")]
    [InlineData("**/.claude/worktrees/**", "C:/src/shop/.claude/worktrees/agent-1")]
    [InlineData("**/wt-?", "C:/x/wt-1")]
    [InlineData("**/med åäö/*", "C:/x/med åäö/y")]
    [InlineData("*", "anything")]
    [InlineData("a/**/b", "a/b")]
    [InlineData("a/**/b", "a/x/y/b")]
    public void Matches(string pattern, string path)
    {
        Assert.True(GlobMatcher.IsMatch(pattern, path, ignoreCase: false));
    }

    [Theory]
    [InlineData("claude/*", "claude/nested/branch")]
    [InlineData("claude/*", "my-claude/x")]
    [InlineData("claude/*", "claude")]
    [InlineData("**/copilot-worktrees/*", "C:/repos/copilot-worktrees")]
    [InlineData("**/copilot-worktrees/*", "C:/repos/copilot-worktrees/shop/wt")]
    [InlineData("**/copilot-worktrees/*/copilot-prewarm-*", "C:/repos/copilot-worktrees/shop/me-curly-waddle")]
    [InlineData("**/.claude/worktrees", "C:/src/shop/.claude")]
    [InlineData("**/wt-?", "C:/x/wt-12")]
    [InlineData("a/**/b", "a/x/c")]
    public void Does_not_match(string pattern, string path)
    {
        Assert.False(GlobMatcher.IsMatch(pattern, path, ignoreCase: false));
    }

    [Fact]
    public void Case_only_matters_when_asked()
    {
        Assert.True(GlobMatcher.IsMatch("**/Copilot-Worktrees/*", "c:/REPOS/copilot-worktrees/Shop", ignoreCase: true));
        Assert.True(GlobMatcher.IsMatch("**/åäö", "C:/x/ÅÄÖ", ignoreCase: true));
        Assert.False(GlobMatcher.IsMatch("Claude/*", "claude/x", ignoreCase: false));
    }

    [Fact]
    public void Any_of_several_patterns()
    {
        Assert.True(GlobMatcher.IsMatchAny(["claude/*", "copilot/*"], "copilot/x", ignoreCase: false));
        Assert.False(GlobMatcher.IsMatchAny([], "copilot/x", ignoreCase: false));
    }
}
