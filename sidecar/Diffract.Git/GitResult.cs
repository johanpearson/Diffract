using System.Text;

namespace Diffract.Git;

public sealed record GitResult(
    IReadOnlyList<string> Arguments,
    int ExitCode,
    byte[] StandardOutputBytes,
    string StandardError,
    TimeSpan Duration)
{
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
