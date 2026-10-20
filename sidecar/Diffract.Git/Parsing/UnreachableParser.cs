namespace Diffract.Git.Parsing;

/// <param name="Type">"commit", "tree", "blob" or "tag".</param>
public sealed record UnreachableObject(string Type, string Id);

/// <summary>Parses <c>git fsck --unreachable --no-reflogs</c>.</summary>
public static class UnreachableParser
{
    public static IReadOnlyList<string> Arguments { get; } = ["fsck", "--unreachable", "--no-reflogs", "--no-progress"];

    // fsck has no machine-readable mode, but these lines are stable: "unreachable <type> <id>".
    // Other lines (for example "missing" or "broken link" in a damaged repo) are not about unreachable objects.
    public static IReadOnlyList<UnreachableObject> Parse(string output)
    {
        var objects = new List<UnreachableObject>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split(' ');
            if (parts is ["unreachable", var type, var id])
                objects.Add(new UnreachableObject(type, id));
        }
        return objects;
    }
}
