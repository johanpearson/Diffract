namespace Diffract.Git;

public class GitException(string message) : Exception(message);

public sealed class GitCommandException(IReadOnlyList<string> arguments, int exitCode, string standardError)
    : GitException($"git {string.Join(' ', arguments)} failed with exit code {exitCode}: {standardError.Trim()}")
{
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}

public sealed class GitTimeoutException(IReadOnlyList<string> arguments, TimeSpan timeout)
    : GitException($"git {string.Join(' ', arguments)} did not finish within {timeout.TotalSeconds:0.#} seconds")
{
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public TimeSpan Timeout { get; } = timeout;
}

public sealed class ReadOnlyViolationException(IReadOnlyList<string> arguments, string reason)
    : GitException($"git {string.Join(' ', arguments)} is not allowed in read-only mode: {reason}")
{
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public string Reason { get; } = reason;
}

public sealed class ChangesPolicyViolationException(IReadOnlyList<string> arguments, string reason)
    : GitException($"git {string.Join(' ', arguments)} is not an allowed change: {reason}")
{
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public string Reason { get; } = reason;
}

/// <summary>Another git process holds the worktree's index lock, and still did after waiting.</summary>
public sealed class IndexLockedException()
    : GitException("Another git process is using this repo, probably an agent. Try again in a moment.");

/// <summary>What the user saw is no longer what is there, so nothing was changed.</summary>
public sealed class ChangedSinceShownException(string path)
    : GitException($"{path} changed since it was shown, so nothing was staged or unstaged. Look at it again.")
{
    public string Path { get; } = path;
}

/// <summary>Git did not commit, or Diffract did not ask it to. Says why in plain words, with git's own output when there is some.</summary>
public sealed class CommitRefusedException(string reason, string? gitOutput = null)
    : GitException(gitOutput is null ? reason : $"{reason}\n\n{gitOutput.Trim()}")
{
    public string Reason { get; } = reason;
    public string? GitOutput { get; } = gitOutput;
}

/// <param name="detail">Why, when there is more to say than "not a repo", as a full sentence.</param>
public sealed class NotAGitRepositoryException(string path, string? detail = null)
    : GitException(detail is null ? $"Not a git repository: {path}" : $"Not a git repository: {path}. {detail}")
{
    public string Path { get; } = path;
}

public sealed class GitVersionTooOldException(GitVersion found)
    : GitException($"Diffract needs git {GitVersion.Minimum} or newer, but found {found}. Update git from https://git-scm.com.")
{
    public GitVersion Found { get; } = found;
}

/// <summary>A fetch, pull or push that could not run or did not finish. Says why in plain words, with git's own output when there is some.</summary>
public sealed class RemoteFailedException(string reason, string? gitOutput = null)
    : GitException(gitOutput is null ? reason : $"{reason}\n\n{gitOutput.Trim()}")
{
    public string Reason { get; } = reason;
    public string? GitOutput { get; } = gitOutput;
}
