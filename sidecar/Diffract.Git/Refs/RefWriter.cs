using System.Text;
using Diffract.Git.Changes;

namespace Diffract.Git.Refs;

/// <summary>Where to switch to: a local branch, a remote-tracking branch to make a local one for, or a commit.</summary>
public abstract record SwitchTarget
{
    private SwitchTarget()
    {
    }

    /// <summary>A local branch, such as "main".</summary>
    public sealed record Branch(string Name) : SwitchTarget;

    /// <summary>A remote-tracking branch, such as "origin/topic". Git creates "topic" to follow it.</summary>
    public sealed record RemoteBranch(string Name) : SwitchTarget
    {
        /// <summary>"topic" for "origin/topic", as <c>git switch --track</c> names it.</summary>
        public string LocalName => Name[(Name.IndexOf('/') + 1)..];
    }

    /// <summary>A commit, checked out on a detached HEAD.</summary>
    public sealed record Commit(string Id) : SwitchTarget;
}

/// <summary>A new branch or tag: its name, and the full id of the commit it starts at.</summary>
public sealed record NewRef(string Name, string Commit);

/// <summary>A ref that was made or checked out, and what happened, in plain words.</summary>
/// <param name="Ref">Such as "refs/heads/topic". Null after switching to a detached HEAD.</param>
public sealed record RefChange(string? Ref, string Message);

/// <summary>
/// Creates branches and tags, and switches what one worktree has checked out, through a changes runner. Plain
/// <c>git switch</c> refuses rather than overwrite uncommitted changes, and then git's words are passed on.
/// </summary>
public sealed class RefWriter
{
    private const string SwitchRefused = "Git did not switch, so nothing changed. Git refuses rather than overwrite uncommitted changes. This is what git said:";

    private readonly GitRunner _changes;
    private readonly GitRunner _reads;
    private readonly IndexLockRetry _retry;

    public RefWriter(string worktree, GitRunnerOptions runnerOptions)
    {
        _changes = new GitRunner(worktree, runnerOptions with { ChangesOnly = true });
        _reads = new GitRunner(worktree, runnerOptions with { ChangesOnly = false, ReadOnly = true });
        _retry = new IndexLockRetry(runnerOptions.TimeProvider);
    }

    /// <summary>A new branch at the commit, checked out too when <paramref name="switchTo"/> is set.</summary>
    public async Task<RefChange> CreateBranchAsync(NewRef branch, bool switchTo, CancellationToken ct)
    {
        await CheckNameAsync(RefNameKind.Branch, branch, ct).ConfigureAwait(false);
        string[] args = switchTo ? ["switch", "-c", branch.Name, branch.Commit] : ["branch", branch.Name, branch.Commit];
        await RunAsync(new GitWrite(new GitCommandLine(args, []), "Git did not create the branch. This is what git said:"), ct).ConfigureAwait(false);
        var made = $"Created branch {branch.Name} at {ShortId.Of(branch.Commit)}";
        return new RefChange(RefNames.RefOf(RefNameKind.Branch, branch.Name), switchTo ? $"{made}, and switched to it." : $"{made}.");
    }

    /// <summary>An annotated tag at the commit. The message is the tag's name when none is given.</summary>
    public async Task<RefChange> CreateTagAsync(NewRef tag, string? message, CancellationToken ct)
    {
        await CheckNameAsync(RefNameKind.Tag, tag, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(message) ? tag.Name : message);
        var command = GitCommandLine.WithBytes(["tag", "-a", tag.Name, tag.Commit, "-F", "-"], text);
        await RunAsync(new GitWrite(command, "Git did not create the tag. This is what git said:"), ct).ConfigureAwait(false);
        return new RefChange(RefNames.RefOf(RefNameKind.Tag, tag.Name), $"Created tag {tag.Name} at {ShortId.Of(tag.Commit)}.");
    }

    public async Task<RefChange> SwitchAsync(SwitchTarget target, CancellationToken ct)
    {
        var (args, done) = target switch
        {
            SwitchTarget.Branch b => (new[] { "switch", b.Name }, $"Switched to {b.Name}."),
            SwitchTarget.RemoteBranch r => (["switch", "--track", r.Name], $"Created {r.LocalName} to follow {r.Name}, and switched to it."),
            SwitchTarget.Commit c => (["switch", "--detach", c.Id], $"Checked out {ShortId.Of(c.Id)} on a detached HEAD."),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        await RunAsync(new GitWrite(new GitCommandLine(args, []), SwitchRefused), ct).ConfigureAwait(false);
        var head = await HeadState.ReadAsync(_reads, ct).ConfigureAwait(false);
        return new RefChange(head.Branch is { } branch ? RefNames.RefOf(RefNameKind.Branch, branch) : null, done);
    }

    private async Task CheckNameAsync(RefNameKind kind, NewRef wanted, CancellationToken ct)
    {
        if (await RefNames.ProblemAsync(_reads, kind, wanted.Name, ct).ConfigureAwait(false) is { } problem)
            throw new ChangeRefusedException(problem);
    }

    /// <summary>
    /// A switch writes the index, so it waits for a lock as staging does. The others only add a ref. A value the
    /// changes allowlist would refuse, such as an id that is not a full one, is the caller's mistake.
    /// </summary>
    private async Task RunAsync(GitWrite write, CancellationToken ct)
    {
        var args = write.Command.Arguments;
        if (!ChangesPolicy.IsAllowed(args, out _))
            throw new ArgumentException($"Not a name or full commit id git can take here: {string.Join(' ', args.Skip(1))}");
        var result = await _retry.RunAsync(_changes, write.Command, [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ChangeRefusedException(write.Refused, result.StandardError);
    }

    /// <summary>A write, and what to say before git's own words if git refuses it.</summary>
    private sealed record GitWrite(GitCommandLine Command, string Refused);
}
