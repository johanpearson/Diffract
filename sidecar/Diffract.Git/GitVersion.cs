using System.Globalization;
using System.Text.RegularExpressions;

namespace Diffract.Git;

public sealed partial record GitVersion(int Major, int Minor, int Patch) : IComparable<GitVersion>
{
    public static GitVersion Minimum { get; } = new(2, 40, 0);

    public bool IsSupported => CompareTo(Minimum) >= 0;

    /// <summary>Parses the output of <c>git version</c>, for example "git version 2.47.1.windows.1".</summary>
    public static GitVersion Parse(string output)
    {
        var match = VersionPattern().Match(output);
        if (!match.Success)
            throw new FormatException($"Not git version output: '{output.Trim()}'");

        return new GitVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
    }

    public static async Task<GitVersion> DetectAsync(GitRunner runner, CancellationToken ct)
    {
        var result = await runner.RunAsync(["version"], ct).ConfigureAwait(false);
        return Parse(result.StandardOutput);
    }

    /// <summary>Detects the git version and throws <see cref="GitVersionTooOldException"/> below the minimum.</summary>
    public static async Task<GitVersion> RequireSupportedAsync(GitRunner runner, CancellationToken ct)
    {
        var version = await DetectAsync(runner, ct).ConfigureAwait(false);
        return version.IsSupported ? version : throw new GitVersionTooOldException(version);
    }

    public int CompareTo(GitVersion? other) =>
        other is null ? 1 : (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"^git version (\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
