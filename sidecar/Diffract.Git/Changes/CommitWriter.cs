using System.Text;

namespace Diffract.Git.Changes;

/// <param name="Message">From the commit box: a subject, then optionally a blank line and a body.</param>
/// <param name="Amend">Replace the last commit instead of adding one. Refused when it is already pushed.</param>
public sealed record CommitRequest(string Message, bool Amend);

/// <summary>
/// Commits what is staged in one worktree, through a changes runner. Hooks run as they always do and signing follows
/// the repo's config. Diffract checks what it can first, from machine-readable answers, so most refusals come in plain
/// words before git runs. What is left, such as a failing hook or signature, comes with git's own output.
/// </summary>
public sealed class CommitWriter
{
    /// <summary>A pre-commit hook may run linters or tests, so a commit may take far longer than other git calls.</summary>
    public static readonly TimeSpan HookPatience = TimeSpan.FromMinutes(10);

    private static readonly string[] Commit = ["commit", "-F", "-"];
    private static readonly string[] Amend = ["commit", "--amend", "-F", "-"];

    private readonly GitRunner _changes;
    private readonly GitRunner _reads;
    private readonly IndexLockRetry _retry;

    public CommitWriter(string worktree, GitRunnerOptions runnerOptions)
    {
        _changes = new GitRunner(worktree, runnerOptions with { ChangesOnly = true, Timeout = HookPatience });
        _reads = new GitRunner(worktree, runnerOptions with { ChangesOnly = false, ReadOnly = true });
        _retry = new IndexLockRetry(runnerOptions.TimeProvider);
    }

    /// <summary>
    /// The checks run side by side: identity, and something staged or, for amend, a commit that is not pushed. Then
    /// the commit itself, with the message on standard input exactly as written.
    /// </summary>
    public async Task CommitAsync(CommitRequest request, CancellationToken ct)
    {
        var message = MessageBytes(request.Message);
        await Task.WhenAll(CheckIdentityAsync(ct), request.Amend ? CheckAmendAsync(ct) : CheckStagedAsync(ct)).ConfigureAwait(false);
        var command = GitCommandLine.WithBytes(request.Amend ? Amend : Commit, message);
        var result = await _retry.RunAsync(_changes, command, [1, 128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new CommitRefusedException("Git did not commit. A hook or commit signing can stop a commit. This is what git said:", Output(result));
    }

    /// <summary>A message needs a subject: a first line with something on it.</summary>
    private static byte[] MessageBytes(string message)
    {
        if (string.IsNullOrWhiteSpace(message.Split('\n')[0]))
            throw new ArgumentException("A commit needs a subject on its first line.", nameof(message));
        return Encoding.UTF8.GetBytes(message);
    }

    /// <summary><c>git var</c> fails exactly when git would refuse to commit for want of a name or email.</summary>
    private async Task CheckIdentityAsync(CancellationToken ct)
    {
        var result = await _reads.RunAsync(["var", "GIT_AUTHOR_IDENT"], [128], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new CommitRefusedException("Git doesn't know your name and email, so it can't commit. Set user.name and user.email in Git's settings.", result.StandardError);
    }

    private async Task CheckStagedAsync(CancellationToken ct)
    {
        var result = await _reads.RunAsync(["diff", "--cached", "--quiet"], [1], ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
            throw new CommitRefusedException("Nothing is staged, so there is nothing to commit.");
    }

    private async Task CheckAmendAsync(CancellationToken ct)
    {
        var head = await _reads.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], [1], ct).ConfigureAwait(false);
        if (head.ExitCode != 0)
            throw new CommitRefusedException("There is no commit to amend yet.");
        var remote = HeadOnRemote.Parse(await _reads.RunAsync(HeadOnRemote.Arguments, ct).ConfigureAwait(false));
        if (remote is not null)
            throw new CommitRefusedException($"The last commit is already on {remote}, so amending it would rewrite pushed work.");
    }

    /// <summary>A hook may write to either stream, so both are shown.</summary>
    private static string Output(GitResult result) => string.Join("\n", new[] { result.StandardOutput, result.StandardError }.Where(s => s.Trim().Length > 0)).Trim();
}
