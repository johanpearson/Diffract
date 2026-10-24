namespace Diffract.Git.Refs;

/// <summary>What a new name is for.</summary>
public enum RefNameKind
{
    Branch,
    Tag,
}

/// <summary>
/// Checks a new branch or tag name before anything runs: whether git takes it, and whether it is already used. Only
/// reads. Git's own rules come from <c>git check-ref-format</c>, never from a copy of them.
/// </summary>
public static class RefNames
{
    private static readonly Dictionary<RefNameKind, (string Prefix, string Noun)> Kinds = new()
    {
        [RefNameKind.Branch] = ("refs/heads/", "branch"),
        [RefNameKind.Tag] = ("refs/tags/", "tag"),
    };

    /// <summary>The full ref, such as "refs/heads/topic".</summary>
    public static string RefOf(RefNameKind kind, string name) => Kinds[kind].Prefix + name;

    /// <summary>Why the name cannot be used, in plain words, or null when it can.</summary>
    public static async Task<string?> ProblemAsync(GitRunner runner, RefNameKind kind, string name, CancellationToken ct)
    {
        var noun = Kinds[kind].Noun;
        if (name.Trim().Length == 0)
            return $"A {noun} needs a name.";
        if (name[0] is '-' or '+')
            return $"A {noun} name cannot start with \"{name[0]}\".";
        var full = RefOf(kind, name);
        var format = runner.RunAsync(["check-ref-format", full], [1], ct);
        var taken = runner.RunAsync(["rev-parse", "--verify", "--quiet", full], [1], ct);
        await Task.WhenAll(format, taken).ConfigureAwait(false);
        if ((await format.ConfigureAwait(false)).ExitCode != 0)
            return $"\"{name}\" is not a name git allows for a {noun}. Spaces, \"..\", \"~\", \"^\", \":\", \"?\", \"*\" and \"[\" are not allowed.";
        return (await taken.ConfigureAwait(false)).ExitCode == 0 ? $"There is already a {noun} named {name}." : null;
    }
}
