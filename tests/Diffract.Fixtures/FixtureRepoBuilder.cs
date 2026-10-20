using Diffract.Git.Snapshot;

namespace Diffract.Fixtures;

/// <summary>A built scenario. Everything else it made (worktrees, origin, folders) sits next to <see cref="RepoPath"/>.</summary>
public sealed record FixtureScenario(string Name, string RepoPath);

/// <summary>
/// Builds the milestone 1 scenarios, one folder per scenario under <paramref name="root"/>.
/// Every date is relative to <see cref="Now"/>, so rules evaluated at that time see the ages the scenario names.
/// </summary>
public sealed class FixtureRepoBuilder(TempDirectory root, DateTimeOffset now)
{
    /// <summary>The "now" tests build with. Rules under test should run with a clock set to this.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly (string Name, Func<FixtureRepoBuilder, Task<FixtureScenario>> Build)[] Scenarios =
    [
        ("Clean", b => b.CleanAsync()),
        ("MergedWorktree", b => b.MergedWorktreeAsync()),
        ("PrunableWorktree", b => b.PrunableWorktreeAsync()),
        ("StaleAgentWorktree", b => b.StaleAgentWorktreeAsync()),
        ("ActiveAgentWorktree", b => b.ActiveAgentWorktreeAsync()),
        ("LockedWorktree", b => b.LockedWorktreeAsync()),
        ("UserDirtyWorktree", b => b.UserDirtyWorktreeAsync()),
        ("AgentBranchMerged", b => b.AgentBranchMergedAsync()),
        ("AgentBranchUnmerged", b => b.AgentBranchUnmergedAsync()),
        ("RemoteAgentBranch", b => b.RemoteAgentBranchAsync()),
        ("ToolStash", b => b.ToolStashAsync()),
        ("StaleCheckpointSession", b => b.StaleCheckpointSessionAsync()),
        ("RecentCheckpointSession", b => b.RecentCheckpointSessionAsync()),
        ("SquashMergedBranch", b => b.SquashMergedBranchAsync()),
        ("PrewarmWorktree", b => b.PrewarmWorktreeAsync()),
        ("LeftoverWorktreeFolder", b => b.LeftoverWorktreeFolderAsync()),
        ("OrphanedCommit", b => b.OrphanedCommitAsync()),
        ("OldArchiveTag", b => b.OldArchiveTagAsync()),
        ("RecentArchiveTag", b => b.RecentArchiveTagAsync()),
        ("AwkwardPaths", b => b.AwkwardPathsAsync()),
        ("DetachedWorktree", b => b.DetachedWorktreeAsync()),
        ("MasterAsMain", b => b.MasterAsMainAsync()),
    ];

    public FixtureRepoBuilder(TempDirectory root) : this(root, DefaultNow)
    {
    }

    public static IReadOnlyList<string> ScenarioNames { get; } = [.. Scenarios.Select(s => s.Name)];

    public DateTimeOffset Now { get; } = now;

    /// <summary>Builds a scenario by name, ignoring case.</summary>
    public Task<FixtureScenario> BuildAsync(string name)
    {
        foreach (var scenario in Scenarios)
        {
            if (scenario.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return scenario.Build(this);
        }

        throw new ArgumentException($"Unknown scenario '{name}'. Known: {string.Join(", ", ScenarioNames)}", nameof(name));
    }

    // --- scenarios --------------------------------------------------------------

    public async Task<FixtureScenario> CleanAsync()
    {
        var repo = await NewRepoAsync("Clean");
        repo.Clock.Now = DaysAgo(30);
        await repo.CommitAsync("src/app.txt", "app", "Add app");
        return Done(repo);
    }

    public async Task<FixtureScenario> MergedWorktreeAsync()
    {
        var repo = await NewRepoAsync("MergedWorktree");
        var path = Folder("MergedWorktree", "worktrees", "feature-done");
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/done", path);
        repo.Clock.Now = DaysAgo(5);
        await repo.At(path).CommitAsync("feature.txt", "done", "Finish feature");
        repo.Clock.Now = DaysAgo(4);
        await repo.GitAsync("merge", "-q", "--no-ff", "-m", "Merge feature/done", "feature/done");
        return Done(repo, (path, DaysAgo(4)));
    }

    public async Task<FixtureScenario> PrunableWorktreeAsync()
    {
        var repo = await NewRepoAsync("PrunableWorktree");
        var path = Folder("PrunableWorktree", "worktrees", "deleted");
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/deleted", path);
        Directory.Delete(path, recursive: true);
        return Done(repo);
    }

    public async Task<FixtureScenario> StaleAgentWorktreeAsync()
    {
        var repo = await NewRepoAsync("StaleAgentWorktree");
        var path = ClaudeWorktree(repo, "refactor-cart");
        await repo.GitAsync("worktree", "add", "-q", "-b", "claude/refactor-cart", path);
        var worktree = repo.At(path);
        repo.Clock.Now = DaysAgo(10);
        await worktree.CommitAsync("cart.txt", "v1", "Refactor cart");
        repo.Clock.Now = DaysAgo(9);
        await worktree.CommitAsync("cart.txt", "v2", "Refactor cart totals");
        return Done(repo, (path, DaysAgo(9)));
    }

    public async Task<FixtureScenario> ActiveAgentWorktreeAsync()
    {
        var repo = await NewRepoAsync("ActiveAgentWorktree");
        var path = ClaudeWorktree(repo, "fix-flaky-login");
        await repo.GitAsync("worktree", "add", "-q", "-b", "claude/fix-flaky-login", path);
        var worktree = repo.At(path);
        repo.Clock.Now = DaysAgo(1);
        await worktree.CommitAsync("login.txt", "retry", "Retry flaky login");
        worktree.WriteFile("login.txt", "retry twice", Now.AddMinutes(-1));
        return Done(repo, (path, DaysAgo(1)));
    }

    /// <summary>Old and unmerged, so only the lock says it is in use.</summary>
    public async Task<FixtureScenario> LockedWorktreeAsync()
    {
        var repo = await NewRepoAsync("LockedWorktree");
        var path = ClaudeWorktree(repo, "long-task");
        await repo.GitAsync("worktree", "add", "-q", "-b", "claude/long-task", path);
        repo.Clock.Now = DaysAgo(20);
        await repo.At(path).CommitAsync("task.txt", "step 1", "Start long task");
        await repo.GitAsync("worktree", "lock", "--reason", "Claude Code agent is running", path);
        return Done(repo, (path, DaysAgo(20)));
    }

    /// <summary>Old and with nothing unmerged, so only the uncommitted change says it is in use.</summary>
    public async Task<FixtureScenario> UserDirtyWorktreeAsync()
    {
        var repo = await NewRepoAsync("UserDirtyWorktree");
        var path = Folder("UserDirtyWorktree", "worktrees", "experiment");
        await repo.GitAsync("worktree", "add", "-q", "-b", "experiment", path);
        repo.At(path).WriteFile("readme.md", "# Fixture\nNotes\n", DaysAgo(20));
        return Done(repo, (path, DaysAgo(20)));
    }

    public async Task<FixtureScenario> AgentBranchMergedAsync()
    {
        var repo = await NewRepoAsync("AgentBranchMerged");
        await repo.GitAsync("switch", "-q", "-c", "copilot/update-deps");
        repo.Clock.Now = DaysAgo(12);
        await repo.CommitAsync("deps.txt", "v2", "Update dependencies");
        await repo.GitAsync("switch", "-q", "main");
        repo.Clock.Now = DaysAgo(11);
        await repo.GitAsync("merge", "-q", "--no-ff", "-m", "Merge copilot/update-deps", "copilot/update-deps");
        return Done(repo);
    }

    public async Task<FixtureScenario> AgentBranchUnmergedAsync()
    {
        var repo = await NewRepoAsync("AgentBranchUnmerged");
        await repo.GitAsync("switch", "-q", "-c", "copilot/add-logging");
        foreach (var days in (int[])[11, 10, 9])
        {
            repo.Clock.Now = DaysAgo(days);
            await repo.CommitAsync("log.txt", $"log {days}", $"Add logging, step {12 - days}");
        }

        await repo.GitAsync("switch", "-q", "main");
        return Done(repo);
    }

    /// <summary>The branch exists only as origin/copilot/fix-readme, merged into main like a GitHub PR.</summary>
    public async Task<FixtureScenario> RemoteAgentBranchAsync()
    {
        var repo = await NewRepoWithOriginAsync("RemoteAgentBranch");
        await repo.GitAsync("switch", "-q", "-c", "copilot/fix-readme");
        repo.Clock.Now = DaysAgo(8);
        await repo.CommitAsync("readme.md", "# Fixture\nFixed\n", "Fix readme");
        await repo.GitAsync("push", "-q", "origin", "copilot/fix-readme");
        await repo.GitAsync("switch", "-q", "main");
        repo.Clock.Now = DaysAgo(7);
        await repo.GitAsync("merge", "-q", "--no-ff", "-m", "Merge pull request #12 from copilot/fix-readme", "copilot/fix-readme");
        await repo.GitAsync("push", "-q", "origin", "main");
        await repo.GitAsync("branch", "-q", "-d", "copilot/fix-readme");
        await repo.GitAsync("fetch", "-q", "origin");
        return Done(repo);
    }

    public async Task<FixtureScenario> ToolStashAsync()
    {
        var repo = await NewRepoAsync("ToolStash");
        repo.Clock.Now = DaysAgo(3);
        repo.WriteFile("readme.md", "# Fixture\nHalf done\n");
        await repo.GitAsync("stash", "push", "-q", "-m", "autostash");
        return Done(repo);
    }

    public async Task<FixtureScenario> StaleCheckpointSessionAsync()
    {
        var repo = await NewRepoAsync("StaleCheckpointSession");
        await CheckpointSessionAsync(repo, "3f2b8c1e-7a4d-4e9b-9c61-2d5f0e8a7b13", 11, 10, 9);
        return Done(repo);
    }

    public async Task<FixtureScenario> RecentCheckpointSessionAsync()
    {
        var repo = await NewRepoAsync("RecentCheckpointSession");
        await CheckpointSessionAsync(repo, "9a4e2d71-5c3b-4f8a-b2e6-71d0c9f4a385", 4, 3, 2);
        return Done(repo);
    }

    /// <summary>Pushed, squash-merged on origin, remote branch deleted and pruned: the upstream is gone.</summary>
    public async Task<FixtureScenario> SquashMergedBranchAsync()
    {
        var repo = await NewRepoWithOriginAsync("SquashMergedBranch");
        await repo.GitAsync("switch", "-q", "-c", "feature/search");
        repo.Clock.Now = DaysAgo(6);
        await repo.CommitAsync("search.txt", "v1", "Add search");
        repo.Clock.Now = DaysAgo(5);
        await repo.CommitAsync("search.txt", "v2", "Search filters");
        await repo.GitAsync("push", "-q", "-u", "origin", "feature/search");
        await repo.GitAsync("switch", "-q", "main");
        repo.Clock.Now = DaysAgo(4);
        await repo.GitAsync("merge", "-q", "--squash", "feature/search");
        await repo.GitAsync("commit", "-q", "-m", "Add search (#7)");
        await repo.GitAsync("push", "-q", "origin", "main", ":feature/search");
        await repo.GitAsync("fetch", "-q", "--prune", "origin");
        return Done(repo);
    }

    /// <summary>Old and clean, so only the hidden-path pattern keeps it out of the list.</summary>
    public async Task<FixtureScenario> PrewarmWorktreeAsync()
    {
        var repo = await NewRepoAsync("PrewarmWorktree");
        var path = await AddPrewarmAsync(repo, "PrewarmWorktree");
        return Done(repo, (path, DaysAgo(9)));
    }

    /// <summary>
    /// What a failed <c>git worktree remove</c> leaves on Windows: files but no <c>.git</c> entry.
    /// The registered worktree next to it is a prewarm one, which is never reported, so the folder is the only leftover.
    /// </summary>
    public async Task<FixtureScenario> LeftoverWorktreeFolderAsync()
    {
        var repo = await NewRepoAsync("LeftoverWorktreeFolder");
        var prewarm = await AddPrewarmAsync(repo, "LeftoverWorktreeFolder");
        var leftover = Folder("LeftoverWorktreeFolder", "copilot-worktrees", "repo", "dev-expert-robot");
        WriteFile(Path.Combine(leftover, "src", "App.cs"), "class App;", DaysAgo(9));
        WriteFile(Path.Combine(leftover, "bin", "Debug", "App.dll"), "binary", DaysAgo(9));
        return Done(repo, (prewarm, DaysAgo(9)));
    }

    public async Task<FixtureScenario> OrphanedCommitAsync()
    {
        var repo = await NewRepoAsync("OrphanedCommit");
        repo.Clock.Now = DaysAgo(5);
        await repo.CommitAsync("experiment.txt", "try", "Experiment");
        await repo.GitAsync("reset", "-q", "--hard", "HEAD~1");
        return Done(repo);
    }

    public async Task<FixtureScenario> OldArchiveTagAsync()
    {
        var repo = await NewRepoAsync("OldArchiveTag");
        await ArchiveTagAsync(repo, "old-idea", 45);
        return Done(repo);
    }

    public async Task<FixtureScenario> RecentArchiveTagAsync()
    {
        var repo = await NewRepoAsync("RecentArchiveTag");
        await ArchiveTagAsync(repo, "new-idea", 5);
        return Done(repo);
    }

    /// <summary>Clean worktrees on merged branches in folders with spaces, å ä ö, and a path over 200 characters.</summary>
    public async Task<FixtureScenario> AwkwardPathsAsync()
    {
        var repo = await NewRepoAsync("AwkwardPaths");
        var spaces = Folder("AwkwardPaths", "my worktrees", "feature with spaces");
        var nordic = Folder("AwkwardPaths", "räksmörgås", "åäö ÅÄÖ");
        var longPath = LongPath(Folder("AwkwardPaths", "long"), 210);
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/spaces", spaces);
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/smörgås", nordic);
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/long", longPath);
        return Done(repo, (spaces, DaysAgo(3)), (nordic, DaysAgo(3)), (longPath, DaysAgo(3)));
    }

    /// <summary>Two old detached worktrees at main's tip, one clean and one with an uncommitted change.</summary>
    public async Task<FixtureScenario> DetachedWorktreeAsync()
    {
        var repo = await NewRepoAsync("DetachedWorktree");
        var clean = Folder("DetachedWorktree", "worktrees", "detached-clean");
        var dirty = Folder("DetachedWorktree", "worktrees", "detached-dirty");
        await repo.GitAsync("worktree", "add", "-q", "--detach", clean);
        await repo.GitAsync("worktree", "add", "-q", "--detach", dirty);
        repo.At(dirty).WriteFile("readme.md", "# Fixture\nDetached edit\n", DaysAgo(9));
        return Done(repo, (clean, DaysAgo(9)), (dirty, DaysAgo(9)));
    }

    public async Task<FixtureScenario> MasterAsMainAsync()
    {
        var repo = await NewRepoAsync("MasterAsMain", mainBranch: "master");
        await repo.GitAsync("switch", "-q", "-c", "copilot/fix-typo");
        repo.Clock.Now = DaysAgo(6);
        await repo.CommitAsync("readme.md", "# Fixture\nTypo fixed\n", "Fix typo");
        await repo.GitAsync("switch", "-q", "master");
        repo.Clock.Now = DaysAgo(5);
        await repo.GitAsync("merge", "-q", "--no-ff", "-m", "Merge copilot/fix-typo", "copilot/fix-typo");
        return Done(repo);
    }

    // --- helpers ----------------------------------------------------------------

    private DateTimeOffset DaysAgo(int days) => Now.AddDays(-days);

    private string Folder(string scenario, params string[] parts) => root.Combine([scenario, .. parts]);

    /// <summary>A repo at <c>&lt;scenario&gt;/repo</c> with one commit on main, 60 days old.</summary>
    private async Task<FixtureRepo> NewRepoAsync(string scenario, string mainBranch = "main")
    {
        var repo = await FixtureRepo.InitAsync(root, Path.Combine(scenario, "repo"), mainBranch);
        // Claude Code keeps worktrees inside the repo. Keep them out of main's status, as a .gitignore entry would.
        File.AppendAllText(Path.Combine(repo.Path, ".git", "info", "exclude"), ".claude/worktrees/\n");
        repo.Clock.Now = DaysAgo(60);
        await repo.CommitAsync("readme.md", "# Fixture\n", "Initial commit");
        return repo;
    }

    /// <summary>A repo with a bare origin next to it, main pushed and origin/HEAD set.</summary>
    private async Task<FixtureRepo> NewRepoWithOriginAsync(string scenario)
    {
        var origin = await FixtureRepo.InitAsync(root, Path.Combine(scenario, "origin.git"), bare: true);
        var repo = await NewRepoAsync(scenario);
        await repo.GitAsync("remote", "add", "origin", origin.Path);
        await repo.GitAsync("push", "-q", "-u", "origin", "main");
        await repo.GitAsync("remote", "set-head", "origin", "main");
        return repo;
    }

    private static string ClaudeWorktree(FixtureRepo repo, string name) =>
        Path.Combine(repo.Path, ".claude", "worktrees", name);

    private async Task<string> AddPrewarmAsync(FixtureRepo repo, string scenario)
    {
        var path = Folder(scenario, "copilot-worktrees", "repo", "copilot-prewarm-curly-waddle");
        await repo.GitAsync("worktree", "add", "-q", "--detach", path);
        return path;
    }

    /// <summary>Checkpoint commits as Copilot writes them: snapshots that sit on no branch, one ref each.</summary>
    private async Task CheckpointSessionAsync(FixtureRepo repo, string session, params int[] daysAgo)
    {
        for (var i = 0; i < daysAgo.Length; i++)
        {
            repo.Clock.Now = DaysAgo(daysAgo[i]);
            var checkpoint = $"00000000-0000-4000-8000-{i + 1:D12}";
            var commit = (await repo.GitAsync("commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", $"copilot checkpoint: {session} @ {checkpoint}"))
                .StandardOutput.Trim();
            await repo.GitAsync("update-ref", $"refs/copilot/checkpoints/{session}/{i + 1:D20}/{checkpoint}", commit);
        }
    }

    /// <summary>An annotated <c>archive/</c> tag on one commit that no branch has.</summary>
    private async Task ArchiveTagAsync(FixtureRepo repo, string name, int daysAgo)
    {
        await repo.GitAsync("switch", "-q", "-c", name);
        repo.Clock.Now = DaysAgo(daysAgo + 1);
        await repo.CommitAsync($"{name}.txt", "idea", "Try an idea");
        repo.Clock.Now = DaysAgo(daysAgo);
        await repo.GitAsync("tag", "-a", $"archive/{name}", "-m", "Backup before cleanup");
        await repo.GitAsync("switch", "-q", "main");
        await repo.GitAsync("branch", "-q", "-D", name);
    }

    /// <summary>
    /// Nests short folders until the path is exactly <paramref name="length"/> characters (or one short folder past
    /// <paramref name="start"/> if that is longer). <c>git worktree add</c> on Windows refuses paths past about
    /// 217 characters ("'$GIT_DIR' too big"), even with core.longpaths, so longer worktrees cannot exist.
    /// </summary>
    private static string LongPath(string start, int length)
    {
        var path = start;
        for (var i = 1; path.Length < length; i++)
            path = Path.Combine(path, $"segment-{i:D2}-{new string('x', 30)}");
        return path.Length > length && start.Length < length - 1
            ? path[..length].TrimEnd(Path.DirectorySeparatorChar)
            : path;
    }

    private static void WriteFile(string path, string content, DateTimeOffset modified)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
    }

    /// <summary>
    /// Pins last activity. git stamps index files with the real time, so every worktree's index is set
    /// after the last git call: main to one day ago, the others to the given times.
    /// </summary>
    private FixtureScenario Done(FixtureRepo repo, params (string Path, DateTimeOffset At)[] activity)
    {
        SetIndexTime(repo.Path, DaysAgo(1));
        foreach (var (path, at) in activity)
            SetIndexTime(path, at);

        return new(Path.GetFileName(Path.GetDirectoryName(repo.Path))!, repo.Path);
    }

    private static void SetIndexTime(string worktreePath, DateTimeOffset at)
    {
        var index = GitDirectory.FindIndexFile(worktreePath)
            ?? throw new InvalidOperationException($"No index file for worktree {worktreePath}");
        File.SetLastWriteTimeUtc(index, at.UtcDateTime);
    }
}
