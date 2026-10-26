using Diffract.Git;

namespace Diffract.Leftovers.Cleanup;

public sealed record CleanupOptions
{
    /// <summary>How git runs. The executor makes its own cleanup runner from it, and read-only ones for the checks.</summary>
    public required GitRunnerOptions Git { get; init; }

    public required OperationLog Log { get; init; }

    public IFolderOperations Folders { get; init; } = WindowsFolders.Instance;
}

/// <summary>A cleanup that cannot be undone as asked, such as one the log does not have. Says why in plain words.</summary>
public sealed class CleanupLogException(string message) : Exception(message);

/// <summary>
/// Applies a plan, and undoes what the log says a cleanup did. Every step is checked right before it runs, run through
/// the cleanup runner, and written to the log before the next one starts. A step that is skipped or fails stops the
/// rest of its leftover; other leftovers carry on, since they do not depend on it.
/// </summary>
public sealed class CleanupExecutor(string repoPath, CleanupOptions options)
{
    private const string EarlierStepDidNotRun = "Skipped, as an earlier step for the same leftover did not run.";

    private readonly StepChecks _checks = new(repoPath, options.Git, options.Folders);

    public Task<CleanupResult> ApplyAsync(CleanupPlan plan, CancellationToken ct)
    {
        var leftovers = plan.Leftovers
            .Select((leftover, i) => (Plan: leftover, Index: i))
            .Where(p => p.Plan.Refused is null)
            .Select(p => (IReadOnlyList<PendingStep>)[.. p.Plan.Steps.Select((step, j) => FromPlan(p.Index, j, step))]);
        return RunAsync(Start(null, [.. plan.Leftovers.Select(l => $"{l.Leftover.Kind} {l.Leftover.Name}")]), leftovers, ct);
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

        var undone = history.Where(c => c.Started.Undoes == cleanupId).SelectMany(c => c.Steps).Where(IsDone).Select(Key).ToHashSet();
        List<StepLogged> left = [.. cleanup.Steps.Where(s => IsDone(s) && !undone.Contains(Key(s)))];
        if (!left.Any(s => s.Undo is not null))
            throw new CleanupLogException("Everything in this cleanup that can be undone is undone already.");

        var leftovers = left.GroupBy(s => s.Result.Leftover).OrderByDescending(g => g.Key)
            .Select(g => (IReadOnlyList<PendingStep>)[.. g.Reverse().Select(FromLog)]);
        return RunAsync(Start(cleanupId, cleanup.Started.Leftovers), leftovers, ct);
    }

    private static string NothingUndoable(LoggedCleanup cleanup) =>
        cleanup.Steps.Any(s => IsDone(s) && s.Result.Kind == StepKind.RecycleFolder)
            ? "Nothing this cleanup did can be undone by git. Restore folders from the Recycle Bin."
            : "Nothing this cleanup did can be undone by git.";

    private static bool IsDone(StepLogged step) => step.Result.Outcome == StepOutcome.Done;

    private static (int, int) Key(StepLogged step) => (step.Result.Leftover, step.Result.Step);

    private CleanupStarted Start(string? undoes, IReadOnlyList<string> leftovers) =>
        new(Guid.NewGuid().ToString("N")[..12], repoPath, options.Git.TimeProvider.GetUtcNow(), undoes, leftovers);

    private static PendingStep FromPlan(int leftover, int index, CleanupStep step) => new()
    {
        Result = new StepResult(leftover, index, step.Kind, step.Description, StepOutcome.Done, null),
        Command = step.Command,
        Expects = step.Expects,
        Folder = step.Folder,
        Planned = step,
    };

    private static PendingStep FromLog(StepLogged step)
    {
        var result = step.Result with { Description = $"Undo: {step.Result.Description}" };
        return step.Undo is { } undo
            ? new PendingStep { Result = result, Command = new GitCommandLine(undo.Arguments, undo.Input), Expects = step.UndoExpects }
            : new PendingStep { Result = result, NotUndoable = step.UndoNote ?? "This step cannot be undone." };
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

    /// <summary>A step that cannot be undone is said so, and does not stop the undo of the leftover's other steps.</summary>
    private async Task<List<StepResult>> RunLeftoverAsync(string cleanupId, IReadOnlyList<PendingStep> steps, StepRunner runner, CancellationToken ct)
    {
        List<StepResult> results = [];
        var stopped = false;
        foreach (var step in steps)
        {
            var result = stopped ? step.As(StepOutcome.Skipped, EarlierStepDidNotRun) : await RunOneAsync(step, runner, ct).ConfigureAwait(false);
            Log(cleanupId, step, result);
            results.Add(result);
            stopped |= result.Outcome != StepOutcome.Done && step.NotUndoable is null;
        }

        return results;
    }

    private async Task<StepResult> RunOneAsync(PendingStep step, StepRunner runner, CancellationToken ct)
    {
        if (step.NotUndoable is { } note)
            return step.As(StepOutcome.Skipped, note);
        var check = await _checks.CheckAsync(step.Expects, ct).ConfigureAwait(false);
        if (check.Broken is { } why)
            return step.As(StepOutcome.Skipped, why);
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
