namespace Diffract.Git.Changes;

/// <summary>How a fetch, pull or push ended, when git did not fail.</summary>
public enum RemoteOutcome
{
    /// <summary>Something was fetched, pulled or pushed.</summary>
    Done,

    /// <summary>There was nothing to pull or push.</summary>
    UpToDate,

    /// <summary>Pull did nothing, because the branch and its upstream both have commits the other lacks.</summary>
    Diverged,

    /// <summary>Git refused the push, because the remote branch has commits this one lacks.</summary>
    Rejected,
}

/// <param name="Message">What happened, in plain words, for the bar.</param>
public sealed record RemoteResult(RemoteOutcome Outcome, string Message);

/// <summary>
/// Fetch, pull and push for the branch checked out in one worktree, as decisions 6 and 7 in milestone 4 have them.
/// Pull only fast-forwards, and decides from counts, not from git's messages. Push sends the branch to its upstream,
/// or to origin under the same name, and never forces. Every write goes through a changes runner.
/// </summary>
public sealed class RemoteSync
{
    /// <summary>A fetch may wait on a slow network or a sign-in window.</summary>
    public static readonly TimeSpan NetworkPatience = TimeSpan.FromMinutes(3);

    private const string Origin = "origin";

    private readonly GitRunner _network;
    private readonly GitRunner _pushes;
    private readonly GitRunner _reads;
    private readonly IndexLockRetry _retry;

    /// <summary>Someone watches the commands run, so git is asked to report its progress.</summary>
    private readonly bool _progress;

    /// <param name="patience">How long one network call may take. Null for <see cref="NetworkPatience"/>, and for a push,
    /// which may run a pre-push hook, <see cref="CommitWriter.HookPatience"/>.</param>
    public RemoteSync(string worktree, GitRunnerOptions runnerOptions, TimeSpan? patience = null)
    {
        _network = new GitRunner(worktree, runnerOptions with { ChangesOnly = true, Timeout = patience ?? NetworkPatience });
        _pushes = new GitRunner(worktree, runnerOptions with { ChangesOnly = true, Timeout = patience ?? CommitWriter.HookPatience });
        _reads = new GitRunner(worktree, runnerOptions with { ChangesOnly = false, ReadOnly = true });
        _retry = new IndexLockRetry(runnerOptions.TimeProvider);
        _progress = runnerOptions.OnOutput is not null;
    }

    /// <summary>Fetches the remote of the branch's upstream, or origin when there is none. Never prunes.</summary>
    public async Task<RemoteResult> FetchAsync(CancellationToken ct)
    {
        var tracking = await BranchTracking.ReadAsync(_reads, ct).ConfigureAwait(false);
        var remote = tracking is { HasRemoteUpstream: true } ? tracking.Remote! : await OriginAsync("fetch from", ct).ConfigureAwait(false);
        await FetchFromAsync(remote, ct).ConfigureAwait(false);
        return new RemoteResult(RemoteOutcome.Done, $"Fetched from {remote}.");
    }

    /// <summary>Fetches, then fast-forwards the branch only when it is behind its upstream and not ahead.</summary>
    public async Task<RemoteResult> PullAsync(CancellationToken ct)
    {
        var tracking = Upstream(await CheckedOutAsync("pull", ct).ConfigureAwait(false));
        await FetchFromAsync(tracking.Remote!, ct).ConfigureAwait(false);
        var (ahead, behind) = await CountsAsync(tracking, ct).ConfigureAwait(false);
        if (behind == 0)
            return new RemoteResult(RemoteOutcome.UpToDate, $"Nothing to pull. {tracking.Branch} has everything on {tracking.UpstreamName}.");
        if (ahead > 0)
            return new RemoteResult(RemoteOutcome.Diverged, RemoteMessages.Diverged(tracking, ahead, behind));
        await FastForwardAsync(tracking, ct).ConfigureAwait(false);
        return new RemoteResult(RemoteOutcome.Done, $"Pulled {RemoteMessages.Commits(behind)} from {tracking.UpstreamName}.");
    }

    /// <summary>
    /// Pushes the branch to its upstream. A branch without one goes to origin under the same name, and gets it as its
    /// upstream. A rejected push is an answer, not a failure: the remote has moved on, and pull comes first.
    /// </summary>
    public async Task<RemoteResult> PushAsync(CancellationToken ct)
    {
        var tracking = await CheckedOutAsync("push", ct).ConfigureAwait(false);
        var target = await PushTargetAsync(tracking, ct).ConfigureAwait(false);
        var result = await _pushes.RunAsync(target.Arguments(_progress), [1, 128], ct).ConfigureAwait(false);
        var pushed = PushReport.Parse(result.StandardOutput).FirstOrDefault(r => r.Target == "refs/heads/" + tracking.Branch);
        return RemoteMessages.Pushed(pushed, target) ?? throw new RemoteFailedException(
            $"Git did not push to {target.Name}. A pre-push hook, the network or the remote can stop a push. This is what git said:",
            string.Join("\n", pushed?.Summary, GitOutputSplitter.Readable(result.StandardError)).Trim());
    }

    private async Task<PushTarget> PushTargetAsync(BranchTracking tracking, CancellationToken ct)
    {
        if (!tracking.HasRemoteUpstream)
            return new PushTarget(tracking, await OriginAsync("push to", ct).ConfigureAwait(false), IsNew: true);
        if (!tracking.SameNameOnRemote)
            throw new RemoteFailedException($"{tracking.Branch} follows {tracking.UpstreamName}, which has another name. Diffract only pushes a branch to one of the same name, so push this one from a terminal.");
        return new PushTarget(tracking, tracking.Remote!, IsNew: false);
    }

    private async Task<BranchTracking> CheckedOutAsync(string verb, CancellationToken ct) =>
        await BranchTracking.ReadAsync(_reads, ct).ConfigureAwait(false)
        ?? throw new RemoteFailedException($"No branch with commits is checked out here, so there is nothing to {verb}.");

    private static BranchTracking Upstream(BranchTracking tracking) => tracking.HasRemoteUpstream
        ? tracking
        : throw new RemoteFailedException($"{tracking.Branch} does not follow a branch on a remote, so there is nothing to pull. Push it first.");

    /// <summary>Origin, for a branch with no upstream. Read from config, so a repo without one gets a plain answer.</summary>
    private async Task<string> OriginAsync(string verb, CancellationToken ct)
    {
        var url = await _reads.RunAsync(["config", "--get", $"remote.{Origin}.url"], [1], ct).ConfigureAwait(false);
        return url.ExitCode == 0
            ? Origin
            : throw new RemoteFailedException($"This repo has no remote named {Origin}, so there is nowhere to {verb}.");
    }

    private async Task FetchFromAsync(string remote, CancellationToken ct)
    {
        var result = await _network.RunAsync(_progress ? ["fetch", "--progress", remote] : ["fetch", remote], [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new RemoteFailedException($"Git could not fetch from {remote}. Check the network, and that you can sign in. This is what git said:", GitOutputSplitter.Readable(result.StandardError));
    }

    /// <summary>Commits on the branch that the upstream lacks, and the other way round, after the fetch.</summary>
    private async Task<(int Ahead, int Behind)> CountsAsync(BranchTracking tracking, CancellationToken ct)
    {
        var result = await _reads.RunAsync(["rev-list", "--left-right", "--count", $"HEAD...{tracking.Upstream}"], [128], ct).ConfigureAwait(false);
        var counts = result.StandardOutput.Trim().Split('\t');
        if (result.ExitCode != 0 || counts.Length != 2)
            throw new RemoteFailedException($"{tracking.UpstreamName} is no longer on {tracking.Remote}, so there is nothing to pull.");
        return (int.Parse(counts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(counts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Git refuses rather than overwrite uncommitted changes, and then nothing has changed.</summary>
    private async Task FastForwardAsync(BranchTracking tracking, CancellationToken ct)
    {
        var merge = new GitCommandLine(["merge", "--ff-only", tracking.Upstream!], []);
        var result = await _retry.RunAsync(_network, merge, [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new RemoteFailedException("Git did not pull, so nothing was changed. This is what git said:", result.StandardError);
    }
}
