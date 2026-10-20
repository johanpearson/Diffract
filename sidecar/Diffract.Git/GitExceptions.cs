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

public sealed class GitVersionTooOldException(GitVersion found)
    : GitException($"Diffract needs git {GitVersion.Minimum} or newer, but found {found}. Update git from https://git-scm.com.")
{
    public GitVersion Found { get; } = found;
}
