using System.Text;

namespace Diffract.Git;

/// <summary>A git call that ran to the end, with all of its output.</summary>
/// <param name="Run">How the process ended. A buffered call is never stopped early, so it always has an exit code.</param>
public sealed record GitResult(GitStreamResult Run, byte[] StandardOutputBytes)
{
    public IReadOnlyList<string> Arguments => Run.Arguments;

    public int ExitCode => Run.ExitCode ?? throw new InvalidOperationException("A buffered git call always runs to the end.");

    public string StandardError => Run.StandardError;

    public TimeSpan Duration => Run.Duration;

    public string StandardOutput { get; } = Encoding.UTF8.GetString(StandardOutputBytes);
}

/// <param name="ExitCode">Null when the reader stopped git before it finished.</param>
public sealed record GitStreamResult(
    IReadOnlyList<string> Arguments,
    int? ExitCode,
    string StandardError,
    TimeSpan Duration)
{
    public bool StoppedEarly => ExitCode is null;
}

/// <summary>One git process that was started, whether it succeeded or not. See <see cref="GitRunnerOptions.OnCall"/>.</summary>
public sealed record GitCall(IReadOnlyList<string> Arguments, TimeSpan Duration);
