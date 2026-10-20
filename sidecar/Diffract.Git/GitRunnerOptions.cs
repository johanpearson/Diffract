namespace Diffract.Git;

public sealed record GitRunnerOptions
{
    /// <summary>When true (the default), only read-only commands are allowed. See <see cref="ReadOnlyPolicy"/>.</summary>
    public bool ReadOnly { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra environment variables for every git process. Tests use this to isolate git config.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public string GitExecutable { get; init; } = "git";

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
