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
