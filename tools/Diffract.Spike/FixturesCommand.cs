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
        string? scenario = null;
        int? largeHistory = null;
        var rest = options.Span;
        if (rest is ["--scenario", var requested])
            scenario = requested;
        else if (rest is ["--large-history", var count] && int.TryParse(count, out var commits) && commits > 0)
            largeHistory = commits;
        else if (!rest.IsEmpty)
            return Program.Usage();

        if (scenario is not null && !FixtureRepoBuilder.ScenarioNames.Contains(scenario, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown scenario '{scenario}'. Known: {string.Join(", ", FixtureRepoBuilder.ScenarioNames)}");
            return 1;
        }

        TempDirectory root;
        try
        {
            root = TempDirectory.ForFixtureOutput(outputDir);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        if (largeHistory is { } size)
        {
            var large = await LargeHistory.BuildAsync(root, "large-history", size);
            Console.WriteLine($"{large.Commits} commits, {large.Merges} merges, {large.Branches} branches, {large.Tags} tags: {large.Path}");
            return 0;
        }

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
}
