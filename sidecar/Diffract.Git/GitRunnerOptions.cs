namespace Diffract.Git;

public sealed record GitRunnerOptions
{
    /// <summary>When true (the default), only read-only commands are allowed. See <see cref="ReadOnlyPolicy"/>.</summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>
    /// When true, only the commands in <see cref="ChangesPolicy"/> run, whatever <see cref="ReadOnly"/> says: the
    /// changes Diffract makes to a repo on the user's behalf, and nothing else. Paths are always taken literally.
    /// </summary>
    public bool ChangesOnly { get; init; }

    /// <summary>
    /// When true, only the commands in <see cref="CleanupPolicy"/> run, whatever the other two say: what a cleanup and
    /// its undo change, and nothing else.
    /// </summary>
    public bool CleanupOnly { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra environment variables for every git process. Tests use this to isolate git config.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public string GitExecutable { get; init; } = "git";

    /// <summary>Folders put in front of PATH for every git process. See <see cref="GitInstallation.PathPrefix"/>.</summary>
    public string? PathPrefix { get; init; }

    /// <summary>
    /// When set, git is started where the locator found it, instead of <see cref="GitExecutable"/> and
    /// <see cref="PathPrefix"/>. A git that cannot be started is then looked for again, once, before the call fails.
    /// </summary>
    public GitLocator? Locator { get; init; }

    /// <summary>The git the next call starts.</summary>
    public GitInstallation Installation => Locator?.Current ?? new GitInstallation(GitExecutable, PathPrefix);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// When set, gets each command as it starts and each line it writes to standard error, while it runs, for the
    /// command window. Called from a background thread, in order. Only a <see cref="ChangesOnly"/> or
    /// <see cref="CleanupOnly"/> runner reports:
    /// what Diffract reads to decide what to do is its own business.
    /// </summary>
    public Action<GitOutputLine>? OnOutput { get; init; }

    /// <summary>Called after every git process ends, however it ended. Tests use it to count calls against a budget.</summary>
    public Action<GitCall>? OnCall { get; init; }
}
