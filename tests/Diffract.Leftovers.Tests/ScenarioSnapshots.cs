using System.Collections.Concurrent;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;
using Diffract.Leftovers.Tests;

[assembly: AssemblyFixture(typeof(ScenarioSnapshots))]

namespace Diffract.Leftovers.Tests;

/// <summary>
/// Builds each fixture scenario once per test run and snapshots it with the default config.
/// Rules are pure, so every test can share the same snapshot.
/// </summary>
public sealed class ScenarioSnapshots : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<RepoSnapshot>>> _snapshots = new();

    public Task<RepoSnapshot> GetAsync(string scenario) =>
        _snapshots.GetOrAdd(scenario, name => new Lazy<Task<RepoSnapshot>>(() => BuildAsync(name))).Value;

    private async Task<RepoSnapshot> BuildAsync(string name)
    {
        // Not the test's token: a cancelled test must not poison a snapshot other tests share.
        var built = await new FixtureRepoBuilder(_temp).BuildAsync(name);
        var options = DiffractConfig.Default.ToSnapshotOptions();
        var result = await new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }, options)
            .BuildAsync(built.RepoPath, CancellationToken.None);
        return result.Snapshot;
    }

    public void Dispose() => _temp.Dispose();
}
