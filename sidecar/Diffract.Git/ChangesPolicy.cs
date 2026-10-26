namespace Diffract.Git;

/// <summary>
/// The changes Diffract makes to a repo on the user's behalf, as exact command shapes. Anything else is rejected
/// before a process starts: other subcommands, other options, reads, and values that git could take as an option,
/// a forced refspec or a deletion. Nothing on the list forces, skips hooks or throws a change away.
/// </summary>
public static class ChangesPolicy
{
    /// <summary>Anything that is not empty. Only ever after "--", so git never reads it as an option.</summary>
    private const string PathSlot = "<path>";

    /// <summary>A remote's name.</summary>
    private const string RemoteSlot = "<remote>";

    /// <summary>A branch, tag or remote branch name.</summary>
    private const string RefSlot = "<ref>";

    /// <summary>A full commit id, never a revision expression.</summary>
    private const string CommitSlot = "<commit>";

    private static readonly string[][] Shapes =
    [
        ["add", "--pathspec-from-file=-", "--pathspec-file-nul"],
        ["add", "--intent-to-add", "--", PathSlot],
        ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"],
        // Unstaging before the first commit, when there is no HEAD to restore from. The files stay on disk.
        ["rm", "--cached", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"],
        ["apply", "--cached", "--unidiff-zero", "-"],
        ["apply", "--cached", "--unidiff-zero", "--reverse", "-"],
        ["commit", "-F", "-"],
        ["commit", "--amend", "-F", "-"],
        ["fetch", RemoteSlot],
        // With --progress, git reports progress even though no terminal is reading it, for the command window.
        ["fetch", "--progress", RemoteSlot],
        ["merge", "--ff-only", RefSlot],
        ["push", "--porcelain", RemoteSlot, RefSlot],
        ["push", "--porcelain", "--set-upstream", RemoteSlot, RefSlot],
        ["push", "--porcelain", "--progress", RemoteSlot, RefSlot],
        ["push", "--porcelain", "--progress", "--set-upstream", RemoteSlot, RefSlot],
        ["branch", RefSlot, CommitSlot],
        ["switch", RefSlot],
        ["switch", "-c", RefSlot, CommitSlot],
        ["switch", "--track", RefSlot],
        ["switch", "--detach", CommitSlot],
        ["tag", "-a", RefSlot, CommitSlot, "-F", "-"],
        ["reset", "--soft", CommitSlot],
        ["reset", "--mixed", CommitSlot],
    ];

    /// <summary>Every allowed command, with a placeholder such as "&lt;commit&gt;" for each value.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllowedShapes { get; } = Shapes;

    public static bool IsAllowed(IReadOnlyList<string> args, out string reason)
    {
        reason = Shapes.Any(shape => Matches(shape, args)) ? "" : "it is not on the changes allowlist";
        return reason.Length == 0;
    }

    private static bool Matches(string[] shape, IReadOnlyList<string> args) =>
        shape.Length == args.Count && shape.Zip(args).All(pair => Fits(pair.First, pair.Second));

    private static bool Fits(string slot, string value) => slot switch
    {
        PathSlot => value.Length > 0,
        CommitSlot => ObjectId.IsValid(value),
        RemoteSlot or RefSlot => IsPlainName(value),
        _ => slot == value,
    };

    /// <summary>Not an option, not a forced refspec such as "+main", not "src:dst" or ":main", and no whitespace.</summary>
    private static bool IsPlainName(string value) =>
        value.Length > 0 && value[0] is not ('-' or '+') && !value.Any(c => c == ':' || char.IsWhiteSpace(c));
}
