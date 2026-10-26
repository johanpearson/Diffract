using System.Collections.Concurrent;
using System.Text.Json;
using Diffract.Git;
using Diffract.Leftovers;
using Diffract.Leftovers.Cleanup;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>What a cleanup needs besides git: the rules, where the operation log lives, and how folders are moved.</summary>
internal sealed record CleanupSetup(DiffractConfig Config, OperationLog Log, IFolderOperations Folders);

/// <summary>
/// Plan, then apply. <c>cleanup.plan</c> keeps the plan and answers with its id; <c>cleanup.apply</c> runs that plan,
/// once, so the UI can never apply something other than what the dialog showed. Applying and undoing write only
/// through the cleanup executor, whose runner allows nothing but <see cref="CleanupPolicy"/>.
/// </summary>
internal sealed class CleanupMethods(RepoSessions repos, GitRunnerOptions runnerOptions, CommandOutputs outputs, CleanupSetup setup)
{
    private const string NoLongerALeftover = "It is no longer a leftover. Refresh to see the repo as it is now.";

    /// <summary>A plan the dialog showed, by its id, until it is applied or its repo is closed.</summary>
    private sealed record KeptPlan(string RepoId, CleanupPlan Plan);

    private readonly ConcurrentDictionary<string, KeptPlan> _plans = new();

    /// <summary>Starts no git: it plans from the repo's latest snapshot, which the watcher keeps current.</summary>
    public Task<JsonElement> PlanAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = SidecarMethods.Params<CleanupPlanParams>(parameters);
        var repoId = SidecarMethods.Required(request.RepoId, "repoId");
        var selection = request.Leftovers is { Count: > 0 } given
            ? given
            : throw new RpcException(RpcErrorCodes.InvalidParams, "'leftovers' must list at least one leftover");

        var (snapshot, leftovers) = repos.LeftoversOf(repos.Get(repoId));
        var found = selection.Select(s => leftovers.FirstOrDefault(l => Picks(s, l))).ToList();
        var planned = new Queue<LeftoverPlan>(
            CleanupPlanner.Plan(snapshot, [.. found.OfType<Leftover>()], setup.Config, runnerOptions.TimeProvider).Leftovers);
        var plan = new CleanupPlan([.. found.Select((l, i) => l is null ? LeftoverPlan.Refuse(Gone(selection[i]), NoLongerALeftover) : planned.Dequeue())]);

        var planId = Guid.NewGuid().ToString("N")[..12];
        _plans[planId] = new KeptPlan(repoId, plan);
        return Task.FromResult(SidecarMethods.Result(View(planId, plan)));
    }

    public async Task<JsonElement> ApplyAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = SidecarMethods.Params<CleanupApplyParams>(parameters);
        var repoId = SidecarMethods.Required(request.RepoId, "repoId");
        var planId = SidecarMethods.Required(request.PlanId, "planId");
        try
        {
            var result = await repos.CleanUpAsync(repos.Get(repoId), root => Executor(root, parameters).ApplyAsync(Take(repoId, planId), request.LeaveOut ?? [], ct)).ConfigureAwait(false);
            return SidecarMethods.Result(result);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, e.Message);
        }
    }

    /// <summary>Past cleanups from the operation log, newest first. Starts no git.</summary>
    public Task<JsonElement> HistoryAsync(JsonElement? parameters, CancellationToken ct)
    {
        var repoId = SidecarMethods.Required(SidecarMethods.Params<CleanupHistoryParams>(parameters).RepoId, "repoId");
        return Task.FromResult(SidecarMethods.Result(Executor(repos.SnapshotOf(repoId).RepoRoot, parameters).History()));
    }

    public async Task<JsonElement> UndoAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = SidecarMethods.Params<CleanupUndoParams>(parameters);
        var repoId = SidecarMethods.Required(request.RepoId, "repoId");
        var cleanupId = SidecarMethods.Required(request.CleanupId, "cleanupId");
        return SidecarMethods.Result(await repos.CleanUpAsync(repos.Get(repoId), root => Executor(root, parameters).UndoAsync(cleanupId, ct)).ConfigureAwait(false));
    }

    /// <summary>Forgets the plans of a repo that was closed.</summary>
    public void Forget(string repoId)
    {
        foreach (var (id, kept) in _plans)
        {
            if (kept.RepoId == repoId)
                _plans.TryRemove(id, out _);
        }
    }

    /// <summary>A plan is applied once: taken out, so a second apply of the same id is refused.</summary>
    private CleanupPlan Take(string repoId, string planId) =>
        _plans.TryGetValue(planId, out var kept) && kept.RepoId == repoId && _plans.TryRemove(planId, out _)
            ? kept.Plan
            : throw new CleanupLogException("This plan is no longer there, or was applied already. Select the leftovers again.");

    private CleanupExecutor Executor(string repoRoot, JsonElement? parameters) =>
        new(repoRoot, new CleanupOptions { Git = outputs.For(runnerOptions, parameters), Log = setup.Log, Folders = setup.Folders });

    private static bool Picks(SelectedLeftover selected, Leftover leftover) =>
        selected.Kind == leftover.Kind && selected.Name == leftover.Name && SamePath(selected.Path, leftover.Path);

    private static bool SamePath(string? a, string? b) => a is null || b is null
        ? a == b
        : string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>What the UI selected, for a leftover the repo no longer has, so the dialog can still say what it was.</summary>
    private static Leftover Gone(SelectedLeftover selected) => new(
        selected.Kind ?? throw new RpcException(RpcErrorCodes.InvalidParams, "Every leftover needs a 'kind'"),
        selected.Name ?? "", selected.Path, null, Verdict.NeedsReview, null, NoLongerALeftover);

    private static CleanupPlanView View(string planId, CleanupPlan plan) => new(planId,
    [
        .. plan.Leftovers.Select(l => new LeftoverPlanView(l.Leftover.Kind, l.Leftover.Name, l.Leftover.Path, l.Refused, [.. l.Steps.Select(StepView.Of)])),
    ]);
}
