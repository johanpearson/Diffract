using Diffract.Fixtures;

namespace Diffract.Leftovers.Tests;

public class DiffractConfigTests
{
    [Fact]
    public void Default_comes_from_the_embedded_diffract_json()
    {
        var config = DiffractConfig.Default;

        Assert.Equal(("auto", 7, 10), (config.MainBranch, config.StaleAfterDays, config.ActiveWithinMinutes));
        Assert.Equal(("archive/", 30), (config.Archive.TagPrefix, config.Archive.ReviewAfterDays));
        Assert.Equal(["Claude Code", "GitHub Copilot"], config.Agents.Select(a => a.Name));
        Assert.Equal(["refs/copilot/checkpoints/**"], config.Agents[1].RefPatterns);
        Assert.Equal(["Agent host session ", "copilot checkpoint: ", "copilot:dirty-archive:"], config.Agents[1].CommitSubjectPrefixes);
        Assert.Equal(["copilot checkpoint: ", "copilot:dirty-archive:"], config.CheckpointCommitPrefixes);
        Assert.Empty(config.Agents[0].CommitSubjectPrefixes);
        Assert.Equal(["refs/copilot/checkpoints/**", "refs/checkpoints/**"], config.CheckpointRefPatterns);
        Assert.Equal(["**/copilot-worktrees/*/copilot-prewarm-*"], config.HiddenWorktreePathPatterns);
        Assert.Equal(["**/copilot-worktrees/*", "**/.claude/worktrees"], config.WorktreeContainerPatterns);
        Assert.Equal(["autostash", "claude", "copilot"], config.ToolStashPatterns);
        Assert.Equal(["claude/*", "copilot/*"], config.RemoteBranchPatterns);
    }

    [Fact]
    public void Override_replaces_only_the_keys_it_names()
    {
        var config = DiffractConfig.Load("""
            {
              // Comments and trailing commas are fine.
              "staleAfterDays": 14,
              "archive": { "reviewAfterDays": 60 },
              "toolStashPatterns": [ "autostash" ],
            }
            """);

        Assert.Equal((14, 10), (config.StaleAfterDays, config.ActiveWithinMinutes));
        Assert.Equal(("archive/", 60), (config.Archive.TagPrefix, config.Archive.ReviewAfterDays));
        Assert.Equal(["autostash"], config.ToolStashPatterns);
        Assert.Equal(2, config.Agents.Count);
    }

    [Theory]
    [InlineData("""{ "staleAfterDay": 3 }""", "staleAfterDay")]
    [InlineData("""{ "staleAfterDays": "soon" }""", "staleAfterDays")]
    [InlineData("""{ "staleAfterDays": 0 }""", "staleAfterDays")]
    [InlineData("""{ "archive": { "tagPrefix": "" } }""", "tagPrefix")]
    [InlineData("{ not json", "not valid")]
    public void Bad_config_fails_with_a_clear_message(string json, string mentioned)
    {
        var error = Assert.Throws<ConfigException>(() => DiffractConfig.Load(json));

        Assert.Contains(mentioned, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loads_from_a_file()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("diffract.json");
        File.WriteAllText(path, """{ "mainBranch": "trunk" }""");

        Assert.Equal("trunk", DiffractConfig.LoadFile(path).MainBranch);
        Assert.Throws<ConfigException>(() => DiffractConfig.LoadFile(temp.Combine("missing.json")));
    }

    [Fact]
    public void Snapshot_options_follow_the_config()
    {
        var options = DiffractConfig.Load("""{ "mainBranch": "trunk" }""").ToSnapshotOptions();

        Assert.Equal("trunk", options.MainBranch);
        Assert.Equal(DiffractConfig.Default.WorktreeContainerPatterns, options.WorktreeContainerPatterns);
    }
}
