using System.Text;

namespace Diffract.Git;

public enum GitOutputKind
{
    /// <summary>The command about to run, as a person would type it.</summary>
    Command,

    /// <summary>A line git wrote to standard error.</summary>
    Line,

    /// <summary>A progress line git ended with a carriage return: the next line takes its place.</summary>
    Progress,
}

/// <summary>One line of what a git command shows a person while it runs, for the command window.</summary>
public sealed record GitOutputLine(GitOutputKind Kind, string Text)
{
    /// <summary>The command line as a person would type it, with arguments that hold spaces in quotes.</summary>
    public static GitOutputLine CommandOf(IReadOnlyList<string> args) =>
        new(GitOutputKind.Command, string.Join(' ', ["git", .. args.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a)]));
}

/// <summary>
/// Splits git's standard error into lines as it arrives, in pieces cut anywhere. Git redraws a progress line by ending
/// it with a carriage return instead of a line feed; such a line is <see cref="GitOutputKind.Progress"/>. A CR LF pair
/// ends an ordinary line. Empty lines are dropped.
/// </summary>
public sealed class GitOutputSplitter
{
    private readonly StringBuilder _line = new();
    private bool _afterCr;

    /// <summary>Git's standard error without its progress lines, for an error message.</summary>
    public static string Readable(string stderr)
    {
        var splitter = new GitOutputSplitter();
        var lines = splitter.Feed(stderr).Concat(splitter.Flush());
        return string.Join('\n', lines.Where(l => l.Kind == GitOutputKind.Line).Select(l => l.Text));
    }

    /// <summary>The lines that the piece completes.</summary>
    public IReadOnlyList<GitOutputLine> Feed(ReadOnlySpan<char> piece)
    {
        var lines = new List<GitOutputLine>();
        foreach (var c in piece)
            Take(c, lines);
        return lines;
    }

    /// <summary>What is left when git has ended: the last line, if it had no line end.</summary>
    public IReadOnlyList<GitOutputLine> Flush()
    {
        var lines = new List<GitOutputLine>();
        _afterCr = false;
        Emit(GitOutputKind.Line, lines);
        return lines;
    }

    private void Take(char c, List<GitOutputLine> lines)
    {
        if (_afterCr)
        {
            _afterCr = false;
            if (c == '\n')
            {
                Emit(GitOutputKind.Line, lines);
                return;
            }
            Emit(GitOutputKind.Progress, lines);
        }
        if (c == '\r')
            _afterCr = true;
        else if (c == '\n')
            Emit(GitOutputKind.Line, lines);
        else
            _line.Append(c);
    }

    private void Emit(GitOutputKind kind, List<GitOutputLine> lines)
    {
        var text = _line.ToString().TrimEnd();
        _line.Clear();
        if (text.Length > 0)
            lines.Add(new GitOutputLine(kind, text));
    }
}
