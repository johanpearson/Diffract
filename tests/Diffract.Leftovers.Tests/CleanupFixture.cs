using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Parsing;
using Diffract.Git.Snapshot;
using Diffract.Leftovers.Cleanup;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Leftovers.Tests;

/// <summary>Refs, worktrees and stashes as text, to say a repo ended where it started.</summary>
internal sealed record RepoState(string Refs, string Worktrees, string Stashes);

/// <summary>
/// One scenario built in its own temp folder, since a cleanup writes to it, with an operation log next to it. Unlike
/// <see cref="ScenarioSnapshots"/>, nothing is shared between tests.
/// </summary>
internal sealed class CleanupFixture : IDisposable
{
    public static readonly FakeTimeProvider Time = new(FixtureRepoBuilder.DefaultNow);

    private readonly TempDirectory _temp;
    private int _calls;

    private CleanupFixture(TempDirectory temp, string repoPath)
    {
        _temp = temp;
        RepoPath = repoPath;
        Log = new OperationLog(temp.Combine("oplog"));
        Repo = new FixtureRepo(temp, repoPath, new FixtureClock { Now = FixtureRepoBuilder.DefaultNow });
    }

    public string RepoPath { get; }

    public OperationLog Log { get; }

    /// <summary>For changing the repo as an agent would, between planning and applying.</summary>
    public FixtureRepo Repo { get; }

    /// <summary>Git calls the executor made so far, reads included.</summary>
    public int Calls => _calls;

    public GitRunnerOptions Git => new()
    {
        Environment = IsolatedGit.Environment(_temp),
        TimeProvider = Time,
        OnCall = _ => Interlocked.Increment(ref _calls),
    };

    public static async Task<CleanupFixture> BuildAsync(string scenario)
    {
        var temp = new TempDirectory();
        var built = await new FixtureRepoBuilder(temp).BuildAsync(scenario);
        return new CleanupFixture(temp, built.RepoPath);
    }

    public string Combine(params string[] parts) => _temp.Combine(parts);

    public CleanupExecutor Executor(IFolderOperations? folders = null) =>
        new(RepoPath, new CleanupOptions { Git = Git, Log = Log, Folders = folders ?? WindowsFolders.Instance });

    public async Task<RepoSnapshot> SnapshotAsync() =>
        (await new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }, DiffractConfig.Default.ToSnapshotOptions())
            .BuildAsync(RepoPath, CancellationToken.None)).Snapshot;

    public async Task<IReadOnlyList<Leftover>> LeftoversAsync() =>
        new LeftoverClassifier().Classify(await SnapshotAsync(), DiffractConfig.Default, Time);

    /// <summary>Plans every leftover the scenario has, or those <paramref name="select"/> picks.</summary>
    public async Task<CleanupPlan> PlanAsync(Func<Leftover, bool>? select = null)
    {
        var repo = await SnapshotAsync();
        var leftovers = new LeftoverClassifier().Classify(repo, DiffractConfig.Default, Time);
        return CleanupPlanner.Plan(repo, [.. leftovers.Where(select ?? (_ => true))], DiffractConfig.Default, Time);
    }

    public async Task<RepoState> StateAsync()
    {
        var reads = IsolatedGit.ReadOnlyRunner(_temp, RepoPath);
        var refs = await reads.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)"], CancellationToken.None);
        var worktrees = WorktreeListParser.Parse((await reads.RunAsync(WorktreeListParser.Arguments, CancellationToken.None)).StandardOutput);
        var stashes = StashListParser.Parse((await reads.RunAsync(StashListParser.Arguments, CancellationToken.None)).StandardOutput);
        return new RepoState(
            refs.StandardOutput,
            string.Join('\n', worktrees.Select(w => $"{Path.GetFullPath(w.Path)} {w.Head} {w.Branch} {w.IsDetached} {w.IsPrunable}")),
            string.Join('\n', stashes.Select(s => $"{s.CommitId} {s.Message}")));
    }

    public async Task<string> GitAsync(params string[] args) => (await Repo.GitAsync(args)).StandardOutput.Trim();

    public void Dispose() => _temp.Dispose();
}
