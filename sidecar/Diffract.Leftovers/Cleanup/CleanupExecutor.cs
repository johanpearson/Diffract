using Diffract.Git;

namespace Diffract.Leftovers.Cleanup;

public sealed record CleanupOptions
{
    /// <summary>How git runs. The executor makes its own cleanup runner from it, and read-only ones for the checks.</summary>
    public required GitRunnerOptions Git { get; init; }

    public required OperationLog Log { get; init; }

    public IFolderOperations Folders { get; init; } = WindowsFolders.Instance;
}

/// <summary>A cleanup that cannot be applied or undone as asked, such as one the log does not have. Says why in plain words.</summary>
public sealed class CleanupLogException(string message) : Exception(message);

/// <summary>A step by its place in a plan: its leftover's place, then its own place among that leftover's steps.</summary>
public sealed record StepRef(int Leftover, int Step);

/// <summary>A past cleanup or undo, as the history lists it.</summary>
/// <param name="CanUndo">A cleanup that did something git can take back and is not taken back yet. Never an undo.</param>
public sealed record PastCleanup(
    string CleanupId, DateTimeOffset At, string? Undoes, IReadOnlyList<LoggedLeftover> Leftovers, IReadOnlyList<StepResult> Steps, bool CanUndo);

/// <summary>
/// Applies a plan, and undoes what the log says a cleanup did. Every step is checked right before it runs, run through
/// the cleanup runner, and written to the log before the next one starts. A step that is skipped or fails stops the
/// rest of its leftover; other leftovers carry on, since they do not depend on it.
/// </summary>
public sealed class CleanupExecutor(string repoPath, CleanupOptions options)
{
    private const string EarlierStepDidNotRun = "Skipped, as an earlier step for the same leftover did not run.";
    private const string LeftOutByUser = "Left out, as chosen.";

    private readonly StepChecks _checks = new(repoPath, options.Git, options.Folders);

    public Task<CleanupResult> ApplyAsync(CleanupPlan plan, CancellationToken ct) => ApplyAsync(plan, [], ct);

    /// <param name="leaveOut">Optional steps the user unticked, such as deleting a removed worktree's branch.</param>
    public Task<CleanupResult> ApplyAsync(CleanupPlan plan, IReadOnlyCollection<StepRef> leaveOut, CancellationToken ct)
    {
        var steps = plan.Leftovers.SelectMany((l, i) => l.Steps.Select((step, j) => (Ref: new StepRef(i, j), Step: step))).ToList();
        if (leaveOut.FirstOrDefault(r => !steps.Any(s => s.Ref == r && s.Step.Optional)) is { } wrong)
            throw new ArgumentException($"Step {wrong.Step} of leftover {wrong.Leftover} cannot be left out: only a step with its own checkbox can.", nameof(leaveOut));

        var leftovers = plan.Leftovers
            .Select((leftover, i) => (Plan: leftover, Index: i))
            .Where(p => p.Plan.Refused is null)
            .Select(p => (IReadOnlyList<PendingStep>)[.. p.Plan.Steps.Select((step, j) => FromPlan(new StepRef(p.Index, j), step, leaveOut))]);
        return RunAsync(Start(null, [.. plan.Leftovers.Select(l => LoggedLeftover.Of(l.Leftover))]), leftovers, ct);
    }

    /// <summary>
    /// Takes back, in reverse order, every step of the cleanup that was done and is not undone yet, so an undo that
    /// stopped part way can be tried again.
    /// </summary>
    public Task<CleanupResult> UndoAsync(string cleanupId, CancellationToken ct)
    {
        var history = options.Log.History(repoPath);
        var cleanup = history.FirstOrDefault(c => c.Started.CleanupId == cleanupId)
            ?? throw new CleanupLogException($"The operation log of this repo has no cleanup {cleanupId}.");
        if (cleanup.Started.Undoes is not null)
            throw new CleanupLogException("An undo cannot be undone. Clean up again instead.");
        if (!cleanup.Steps.Any(s => IsDone(s) && s.Undo is not null))
            throw new CleanupLogException(NothingUndoable(cleanup));
        var left = StillToUndo(history, cleanup);
        if (!left.Any(s => s.Undo is not null))
            throw new CleanupLogException("Everything in this cleanup that can be undone is undone already.");

        var leftovers = left.GroupBy(s => s.Result.Leftover).OrderByDescending(g => g.Key)
            .Select(g => (IReadOnlyList<PendingStep>)[.. g.Reverse().Select(FromLog)]);
        return RunAsync(Start(cleanupId, cleanup.Started.Leftovers), leftovers, ct);
    }

    /// <summary>Every cleanup and undo of the repo, newest first.</summary>
    public IReadOnlyList<PastCleanup> History()
    {
        var history = options.Log.History(repoPath);
        return
        [
            .. history.Reverse().Select(c => new PastCleanup(
                c.Started.CleanupId, c.Started.At, c.Started.Undoes, c.Started.Leftovers, [.. c.Steps.Select(s => s.Result)],
                c.Started.Undoes is null && StillToUndo(history, c).Any(s => s.Undo is not null))),
        ];
    }

    /// <summary>The cleanup's done steps that no undo of it has taken back yet.</summary>
    private static List<StepLogged> StillToUndo(IReadOnlyList<LoggedCleanup> history, LoggedCleanup cleanup)
    {
        var id = cleanup.Started.CleanupId;
        var undone = history.Where(c => c.Started.Undoes == id).SelectMany(c => c.Steps).Where(IsDone).Select(Key).ToHashSet();
        return [.. cleanup.Steps.Where(s => IsDone(s) && !undone.Contains(Key(s)))];
    }

    private static string NothingUndoable(LoggedCleanup cleanup) =>
        cleanup.Steps.Any(s => IsDone(s) && s.Result.Kind == StepKind.RecycleFolder)
            ? "Nothing this cleanup did can be undone by git. Restore folders from the Recycle Bin."
            : "Nothing this cleanup did can be undone by git.";

    private static bool IsDone(StepLogged step) => step.Result.Outcome == StepOutcome.Done;

    private static StepRef Key(StepLogged step) => new(step.Result.Leftover, step.Result.Step);

    private CleanupStarted Start(string? undoes, IReadOnlyList<LoggedLeftover> leftovers) =>
        new(Guid.NewGuid().ToString("N")[..12], repoPath, options.Git.TimeProvider.GetUtcNow(), undoes, leftovers);

    private static PendingStep FromPlan(StepRef at, CleanupStep step, IReadOnlyCollection<StepRef> leaveOut) => new()
    {
        Result = new StepResult(at.Leftover, at.Step, step.Kind, step.Description, StepOutcome.Done, null),
        Command = step.Command,
        Expects = step.Expects,
        Folder = step.Folder,
        Planned = step,
        NotRun = leaveOut.Contains(at) ? LeftOutByUser : null,
    };

    private static PendingStep FromLog(StepLogged step)
    {
        var result = step.Result with { Description = $"Undo: {step.Result.Description}" };
        return step.Undo is { } undo
            ? new PendingStep { Result = result, Command = new GitCommandLine(undo.Arguments, undo.Input), Expects = step.UndoExpects }
            : new PendingStep { Result = result, NotRun = step.UndoNote ?? "This step cannot be undone." };
    }

    private async Task<CleanupResult> RunAsync(CleanupStarted started, IEnumerable<IReadOnlyList<PendingStep>> leftovers, CancellationToken ct)
    {
        options.Log.Append(repoPath, started);
        var runner = new StepRunner(repoPath, options.Git, options.Folders);
        List<StepResult> results = [];
        foreach (var steps in leftovers)
            results.AddRange(await RunLeftoverAsync(started.CleanupId, steps, runner, ct).ConfigureAwait(false));
        return new CleanupResult(started.CleanupId, results);
    }

    /// <summary>A step not run on purpose, left out or not undoable, does not stop the leftover's other steps.</summary>
    private async Task<List<StepResult>> RunLeftoverAsync(string cleanupId, IReadOnlyList<PendingStep> steps, StepRunner runner, CancellationToken ct)
    {
        List<StepResult> results = [];
        var stopped = false;
        foreach (var step in steps)
        {
            var result = stopped ? step.As(StepOutcome.Skipped, EarlierStepDidNotRun) : await RunOneAsync(step, runner, ct).ConfigureAwait(false);
            Log(cleanupId, step, result);
            results.Add(result);
            stopped |= result.Outcome != StepOutcome.Done && step.NotRun is null;
        }

        return results;
    }

    private async Task<StepResult> RunOneAsync(PendingStep step, StepRunner runner, CancellationToken ct)
    {
        if (step.NotRun is { } why)
            return step.As(StepOutcome.Skipped, why);
        var check = await _checks.CheckAsync(step.Expects, ct).ConfigureAwait(false);
        if (check.Broken is { } broken)
            return step.As(StepOutcome.Skipped, broken);
        var failed = await runner.RunAsync(step, check, ct).ConfigureAwait(false);
        return failed is null ? step.As(StepOutcome.Done, null) : step.As(StepOutcome.Failed, failed);
    }

    /// <summary>A done step keeps what takes it back. Written before the next step runs.</summary>
    private void Log(string cleanupId, PendingStep step, StepResult result)
    {
        var undo = result.Outcome == StepOutcome.Done && step.Planned?.Undo is { } command ? new LoggedCommand(command.Arguments, command.Input) : null;
        options.Log.Append(repoPath, new StepLogged(cleanupId, result, undo, step.Planned?.UndoExpects ?? [], step.Planned?.UndoNote));
    }
}
