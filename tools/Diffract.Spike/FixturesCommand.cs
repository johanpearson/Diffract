using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Spike;

/// <summary>
/// <c>fixtures &lt;output-dir&gt; [--scenario &lt;name&gt; | --large-history &lt;commits&gt;]</c>: builds fixture repos to inspect
/// by hand, dated from the real clock, or one repo with a long generated history for trying history performance.
/// </summary>
internal static class FixturesCommand
{
    public static async Task<int> RunAsync(string outputDir, ReadOnlyMemory<string> options)
    {
        if (FixturesOptions.Parse(options.Span) is not { } parsed)
            return Program.Usage();

        if (parsed.IsUnknownScenario)
        {
            Console.Error.WriteLine($"Unknown scenario '{parsed.Scenario}'. Known: {string.Join(", ", FixtureRepoBuilder.ScenarioNames)}");
            return 1;
        }

        if (OutputRoot(outputDir) is not { } root)
            return 1;

        return parsed.LargeHistory is { } size
            ? await BuildLargeHistoryAsync(root, size)
            : await BuildScenariosAsync(root, parsed.Scenario);
    }

    private static TempDirectory? OutputRoot(string outputDir)
    {
        try
        {
            return TempDirectory.ForFixtureOutput(outputDir);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return null;
        }
    }

    private static async Task<int> BuildLargeHistoryAsync(TempDirectory root, int commits)
    {
        var large = await LargeHistory.BuildAsync(root, "large-history", commits);
        Console.WriteLine($"{large.Commits} commits, {large.Merges} merges, {large.Branches} branches, {large.Tags} tags: {large.Path}");
        return 0;
    }

    /// <param name="scenario">One scenario, or null for all of them.</param>
    private static async Task<int> BuildScenariosAsync(TempDirectory root, string? scenario)
    {
        var builder = new FixtureRepoBuilder(root, TimeProvider.System.GetUtcNow());
        try
        {
            foreach (var name in scenario is null ? FixtureRepoBuilder.ScenarioNames : [scenario])
            {
                var built = await builder.BuildAsync(name);
                Console.WriteLine($"{built.Name,-26}{built.RepoPath}");
            }
        }
        catch (GitCommandException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        return 0;
    }

    private sealed record FixturesOptions(string? Scenario, int? LargeHistory)
    {
        public bool IsUnknownScenario =>
            Scenario is not null && !FixtureRepoBuilder.ScenarioNames.Contains(Scenario, StringComparer.OrdinalIgnoreCase);

        /// <summary>Null when the options are not valid.</summary>
        public static FixturesOptions? Parse(ReadOnlySpan<string> options) => options switch
        {
            [] => new(null, null),
            ["--scenario", var requested] => new(requested, null),
            ["--large-history", var count] when int.TryParse(count, out var commits) && commits > 0 => new(null, commits),
            _ => null,
        };
    }
}
