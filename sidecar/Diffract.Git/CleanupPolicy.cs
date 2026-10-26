using System.Text;

namespace Diffract.Git;

/// <summary>
/// The changes a cleanup makes, and its undo, as exact command shapes: milestone 5's allowlist. Anything else is
/// rejected before a process starts. Nothing on it forces, fetches, pushes or prunes. <c>update-ref --stdin</c> may
/// only create refs, or delete checkpoints and other refs outside branches, tags and remote-tracking branches from an
/// expected value; its standard input is checked for that.
/// </summary>
public static class CleanupPolicy
{
    private static readonly CommandPart[][] Shapes =
    [
        ["worktree", "remove", CommandPart.FullPath],
        ["worktree", "add", CommandPart.FullPath, CommandPart.Ref],
        ["branch", "-D", CommandPart.Ref],
        ["branch", "-d", "-r", CommandPart.Ref],
        ["branch", CommandPart.Ref, CommandPart.Object],
        ["tag", "-a", CommandPart.Ref, CommandPart.Object, "-F", "-"],
        ["tag", "-d", CommandPart.Ref],
        ["stash", "drop", CommandPart.StashEntry],
        ["stash", "store", "-m", CommandPart.Message, CommandPart.Object],
        ["update-ref", "--stdin"],
    ];

    /// <summary>Every allowed command, with a placeholder such as "&lt;object&gt;" for each value.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllowedShapes { get; } = [.. Shapes.Select(shape => shape.Select(part => part.Text).ToList())];

    public static bool IsAllowed(GitCommandLine command, out string reason)
    {
        reason = Refusal(command) ?? "";
        return reason.Length == 0;
    }

    private static string? Refusal(GitCommandLine command)
    {
        if (!Shapes.Any(shape => Matches(shape, command.Arguments)))
            return "it is not on the cleanup allowlist";
        if (command.Arguments[0] == "update-ref" && UpdateRefInput.FirstUnsafe(Encoding.UTF8.GetString(command.InputBytes())) is { } line)
            return $"update-ref may only create refs or delete them from an expected value, not \"{line}\"";
        return null;
    }

    private static bool Matches(CommandPart[] shape, IReadOnlyList<string> args) =>
        shape.Length == args.Count && shape.Zip(args).All(pair => pair.First.Accepts(pair.Second));
}

/// <summary>One part of an allowed command: a word that must be there as it is, or a slot for a value.</summary>
internal sealed record CommandPart(string Text, Func<string, bool> Accepts)
{
    /// <summary>A full Windows path. Never relative, so it can never be read as an option.</summary>
    public static readonly CommandPart FullPath = new("<path>", Path.IsPathFullyQualified);

    /// <summary>A branch, tag or remote-tracking branch name.</summary>
    public static readonly CommandPart Ref = new("<ref>", PolicySlots.IsPlainName);

    /// <summary>A full object id, never a revision expression.</summary>
    public static readonly CommandPart Object = new("<object>", ObjectId.IsValid);

    /// <summary>A stash by its place in the list, such as "stash@{2}".</summary>
    public static readonly CommandPart StashEntry = new("<stash>", IsStashEntry);

    /// <summary>A stash's message, passed as its own argument, so a leading "-" is part of it.</summary>
    public static readonly CommandPart Message = new("<message>", value => value.Length > 0);

    public static implicit operator CommandPart(string word) => new(word, value => value == word);

    private static bool IsStashEntry(string value) =>
        value.StartsWith("stash@{", StringComparison.Ordinal) && value.EndsWith('}') && value["stash@{".Length..^1] is { Length: > 0 } n && n.All(char.IsAsciiDigit);
}
