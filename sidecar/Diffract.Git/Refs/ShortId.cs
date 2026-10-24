namespace Diffract.Git.Refs;

/// <summary>A commit id as people read it: its first 8 characters.</summary>
internal static class ShortId
{
    public static string Of(string commit) => commit[..Math.Min(8, commit.Length)];
}
