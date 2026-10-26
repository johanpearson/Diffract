namespace Diffract.Git;

/// <summary>What <c>update-ref --stdin</c> may be given by a cleanup: one transaction of creates and expected deletes.</summary>
internal static class UpdateRefInput
{
    private static readonly HashSet<string> Controls = ["start", "prepare", "commit"];

    /// <summary>These have their own commands, with a backup tag first. update-ref only puts them back.</summary>
    private static readonly string[] KeptFromDelete = ["refs/heads/", "refs/tags/", "refs/remotes/", "refs/stash"];

    /// <summary>The first line that is not allowed, or null when every line is. Nothing at all is not allowed.</summary>
    public static string? FirstUnsafe(string input)
    {
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "(nothing)" : lines.FirstOrDefault(line => !IsSafe(line));
    }

    private static bool IsSafe(string line) => Controls.Contains(line) || RefLine.Parse(line) is { } parsed && parsed.IsSafe(KeptFromDelete);

    /// <summary>"create &lt;ref&gt; &lt;id&gt;" fails if the ref is there; "delete &lt;ref&gt; &lt;id&gt;" only if it is still at that id.</summary>
    private sealed record RefLine(bool IsDelete, string Name, string Id)
    {
        public static RefLine? Parse(string line) => line.Split(' ') switch
        {
            ["create", var name, var id] => new RefLine(false, name, id),
            ["delete", var name, var id] => new RefLine(true, name, id),
            _ => null,
        };

        public bool IsSafe(string[] keptFromDelete) =>
            Name.StartsWith("refs/", StringComparison.Ordinal) && PolicySlots.IsPlainName(Name)
            && ObjectId.IsValid(Id) && !ObjectId.IsZero(Id)
            && !(IsDelete && keptFromDelete.Any(Name.StartsWith));
    }
}
