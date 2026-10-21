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

    /// <summary>What each attribute after a worktree's first line sets. Unknown ones are ignored, so newer git does not break parsing.</summary>
    private static readonly Dictionary<string, Func<WorktreeEntry, string?, WorktreeEntry>> Attributes = new()
    {
        ["HEAD"] = (w, value) => w with { Head = CommitOrNull(value) },
        ["branch"] = (w, value) => w with { Branch = value },
        ["bare"] = (w, _) => w with { IsBare = true },
        ["detached"] = (w, _) => w with { IsDetached = true },
        ["locked"] = (w, value) => w with { IsLocked = true, LockReason = value },
        ["prunable"] = (w, value) => w with { IsPrunable = true, PrunableReason = value },
    };

    // Each attribute ends with NUL and each worktree ends with an extra NUL. A worktree starts with its 'worktree' line.
    public static IReadOnlyList<WorktreeEntry> Parse(string output)
    {
        var entries = new List<WorktreeEntry>();
        foreach (var field in output.Split('\0').Where(f => f.Length > 0))
        {
            var (key, value) = SplitAttribute(field);
            if (key == "worktree")
                entries.Add(new WorktreeEntry(value ?? "", null, null, false, false, false, null, false, null));
            else if (entries.Count == 0)
                throw new FormatException($"Worktree attribute '{key}' before any 'worktree' line");
            else if (Attributes.TryGetValue(key, out var apply))
                entries[^1] = apply(entries[^1], value);
        }
        return entries;
    }

    // An unborn branch has no commit yet: git prints a HEAD of all zeros.
    private static string? CommitOrNull(string? value) => value is { Length: > 0 } && !ObjectId.IsZero(value) ? value : null;

    private static (string Key, string? Value) SplitAttribute(string field)
    {
        var space = field.IndexOf(' ');
        return space < 0 ? (field, null) : (field[..space], field[(space + 1)..]);
    }
}
