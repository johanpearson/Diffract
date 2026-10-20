namespace Diffract.Git.Parsing;

public sealed record WorktreeEntry(
    string Path,
    string? Head,
    string? Branch,
    bool IsBare,
    bool IsDetached,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    string? PrunableReason);

/// <summary>Parses <c>git worktree list --porcelain -z</c>.</summary>
public static class WorktreeListParser
{
    public static IReadOnlyList<string> Arguments { get; } = ["worktree", "list", "--porcelain", "-z"];

    // Each attribute ends with NUL and each worktree ends with an extra NUL.
    public static IReadOnlyList<WorktreeEntry> Parse(string output)
    {
        var entries = new List<WorktreeEntry>();
        WorktreeEntry? current = null;

        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (current is not null)
                    entries.Add(current);
                current = null;
                continue;
            }

            var (key, value) = SplitAttribute(field);
            if (key == "worktree")
            {
                if (current is not null)
                    entries.Add(current);
                current = new WorktreeEntry(value ?? "", null, null, false, false, false, null, false, null);
                continue;
            }

            if (current is null)
                throw new FormatException($"Worktree attribute '{key}' before any 'worktree' line");

            // Unknown attributes are ignored so newer git versions do not break parsing.
            current = key switch
            {
                "HEAD" => current with { Head = value },
                "branch" => current with { Branch = value },
                "bare" => current with { IsBare = true },
                "detached" => current with { IsDetached = true },
                "locked" => current with { IsLocked = true, LockReason = value },
                "prunable" => current with { IsPrunable = true, PrunableReason = value },
                _ => current,
            };
        }

        if (current is not null)
            entries.Add(current);
        return entries;
    }

    private static (string Key, string? Value) SplitAttribute(string field)
    {
        var space = field.IndexOf(' ');
        return space < 0 ? (field, null) : (field[..space], field[(space + 1)..]);
    }
}
