using Diffract.Git;

namespace Diffract.Fixtures;

/// <summary>
/// Git settings that ignore the developer's own config and make commits deterministic.
/// Every runner a test creates should use these options.
/// </summary>
public static class IsolatedGit
{
    public static readonly DateTimeOffset DefaultDate = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Writes an empty global config file into <paramref name="temp"/> and returns the environment that uses it.</summary>
    public static IReadOnlyDictionary<string, string> Environment(TempDirectory temp, DateTimeOffset? date = null)
    {
        var globalConfig = temp.Combine(".gitconfig-isolated");
        if (!File.Exists(globalConfig))
            File.WriteAllText(globalConfig, "");

        var when = (date ?? DefaultDate).ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = globalConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "Fixture Author",
            ["GIT_AUTHOR_EMAIL"] = "fixture@example.com",
            ["GIT_COMMITTER_NAME"] = "Fixture Author",
            ["GIT_COMMITTER_EMAIL"] = "fixture@example.com",
            ["GIT_AUTHOR_DATE"] = when,
            ["GIT_COMMITTER_DATE"] = when,
        };
    }

    /// <summary>A runner that may write. Throws unless <paramref name="workingDirectory"/> is inside <paramref name="temp"/>.</summary>
    public static GitRunner WritableRunner(TempDirectory temp, string workingDirectory)
    {
        var full = System.IO.Path.GetFullPath(workingDirectory);
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(temp.Path));
        var inside = full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside)
            throw new InvalidOperationException($"Refusing a writable git runner outside the test temp folder: {full}");

        return new(workingDirectory, new GitRunnerOptions { ReadOnly = false, Environment = Environment(temp) });
    }

    public static GitRunner ReadOnlyRunner(TempDirectory temp, string workingDirectory) =>
        new(workingDirectory, new GitRunnerOptions { Environment = Environment(temp) });
}
