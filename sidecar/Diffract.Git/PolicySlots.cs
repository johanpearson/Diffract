namespace Diffract.Git;

/// <summary>What the allowlists that write accept in a slot of a command.</summary>
internal static class PolicySlots
{
    /// <summary>Not an option, not a forced refspec such as "+main", not "src:dst" or ":main", and no whitespace.</summary>
    public static bool IsPlainName(string value) =>
        value.Length > 0 && value[0] is not ('-' or '+') && !value.Any(c => c == ':' || char.IsWhiteSpace(c));
}
