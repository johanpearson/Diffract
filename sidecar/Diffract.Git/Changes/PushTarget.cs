namespace Diffract.Git.Changes;

/// <summary>Where a push goes, and the command that sends it there.</summary>
/// <param name="IsNew">True when the branch has no upstream yet, and gets this one.</param>
internal sealed record PushTarget(BranchTracking Tracking, string Remote, bool IsNew)
{
    /// <summary>Such as "origin/main".</summary>
    public string Name => $"{Remote}/{Tracking.Branch}";

    /// <param name="progress">Asks git to report progress, for someone watching the command run.</param>
    public IReadOnlyList<string> Arguments(bool progress) =>
        ["push", "--porcelain", .. (progress ? ["--progress"] : Array.Empty<string>()), .. (IsNew ? ["--set-upstream"] : Array.Empty<string>()), Remote, Tracking.Branch];
}

/// <summary>The plain words for what a pull or push did.</summary>
internal static class RemoteMessages
{
    public static string Commits(int count) => count == 1 ? "1 commit" : $"{count} commits";

    public static string Diverged(BranchTracking tracking, int ahead, int behind) =>
        $"{tracking.Branch} and {tracking.UpstreamName} have both moved on: {Commits(ahead)} here and {Commits(behind)} there. " +
        "Pull only fast-forwards for now, so nothing was changed.";

    /// <summary>
    /// The result for the branch's line in the push report. Null when there is none, or when the remote refused it,
    /// which is a failure with git's words. "[rejected]" is git's own refusal: the remote has commits this branch lacks.
    /// </summary>
    public static RemoteResult? Pushed(PushedRef? pushed, PushTarget target) => pushed?.State switch
    {
        PushedRefState.FastForward or PushedRefState.New => new(RemoteOutcome.Done, Done(target)),
        PushedRefState.UpToDate => new(RemoteOutcome.UpToDate, $"Nothing to push. {target.Name} already has everything on {target.Tracking.Branch}."),
        PushedRefState.Rejected when pushed.Summary.StartsWith("[rejected]", StringComparison.Ordinal) =>
            new(RemoteOutcome.Rejected, $"{target.Name} has commits that {target.Tracking.Branch} lacks, so git did not push. Pull first, then push."),
        _ => null,
    };

    private static string Done(PushTarget target) => target.IsNew
        ? $"Pushed {target.Tracking.Branch} to {target.Name}, which it now follows."
        : $"Pushed {target.Tracking.Branch} to {target.Name}.";
}
