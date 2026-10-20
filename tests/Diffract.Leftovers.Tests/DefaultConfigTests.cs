using System.Text.Json;
using Diffract.Leftovers;

namespace Diffract.Leftovers.Tests;

public class DefaultConfigTests
{
    [Fact]
    public void Embedded_config_matches_diffract_json_at_repo_root()
    {
        var embedded = DefaultConfig.ReadJson();
        var onDisk = File.ReadAllText(Path.Combine(FindRepoRoot(), "diffract.json"));

        Assert.Equal(onDisk, embedded);
    }

    [Fact]
    public void Embedded_config_is_valid_json_with_agents()
    {
        using var doc = JsonDocument.Parse(DefaultConfig.ReadJson());

        Assert.True(doc.RootElement.GetProperty("agents").GetArrayLength() > 0);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Diffract.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Diffract.slnx not found above the test output folder.");
    }
}
