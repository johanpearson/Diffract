namespace Diffract.Git.Changes;

/// <summary>What happened to one ref in a push, from the flag at the start of its line.</summary>
public enum PushedRefState
{
    /// <summary>Moved forward on the remote.</summary>
    FastForward,

    /// <summary>Created on the remote.</summary>
    New,

    /// <summary>The remote already had it.</summary>
    UpToDate,

    /// <summary>Refused, by git or by the remote.</summary>
    Rejected,

    /// <summary>Forced or deleted. Diffract never asks for either, so it is only listed.</summary>
    Other,
}

/// <param name="Target">The ref on the remote, such as "refs/heads/main".</param>
/// <param name="Summary">Git's summary, such as "[rejected] (fetch first)" or "e46c46e..26d2bca".</param>
public sealed record PushedRef(PushedRefState State, string Target, string Summary);

/// <summary>
/// Reads what <c>git push --porcelain</c> writes to standard output: a "To" line, one line per ref and "Done". A
/// ref's line is its flag, a tab, "source:target", a tab and git's summary. Only the flags are relied on.
/// </summary>
public static class PushReport
{
    private static readonly Dictionary<char, PushedRefState> Flags = new()
    {
        [' '] = PushedRefState.FastForward,
        ['*'] = PushedRefState.New,
        ['='] = PushedRefState.UpToDate,
        ['!'] = PushedRefState.Rejected,
    };

    public static IReadOnlyList<PushedRef> Parse(string output) =>
        [.. output.Split('\n').Select(line => line.TrimEnd('\r').Split('\t')).Where(IsRefLine).Select(RefOf)];

    private static bool IsRefLine(string[] fields) => fields.Length == 3 && fields[0].Length == 1;

    private static PushedRef RefOf(string[] fields) =>
        new(Flags.GetValueOrDefault(fields[0][0], PushedRefState.Other), fields[1][(fields[1].IndexOf(':') + 1)..], fields[2]);
}
