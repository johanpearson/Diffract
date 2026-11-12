namespace Diffract.Git;

/// <summary>What the allowlists that write accept in a slot of a command.</summary>
internal static class PolicySlots
{
    /// <summary>
    /// A full commit id, then ":refs/heads/" and a branch name: the refspec that makes a branch on a remote. Never forced
    /// (no "+"), and never an empty side, which would delete.
    /// </summary>
    public static bool IsCommitAtRemoteBranch(string value) =>
        value.Split(':', 2) is [var id, var target] && ObjectId.IsValid(id)
        && target.StartsWith("refs/heads/", StringComparison.Ordinal) && IsPlainName(target["refs/heads/".Length..]);

    /// <summary>Not an option, not a forced refspec such as "+main", not "src:dst" or ":main", and no whitespace.</summary>
    public static bool IsPlainName(string value) =>
        value.Length > 0 && value[0] is not ('-' or '+') && !value.Any(c => c == ':' || char.IsWhiteSpace(c));
}
