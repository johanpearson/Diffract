namespace Diffract.Git;

/// <summary>Full object ids: 40 hex digits in SHA-1 repos, 64 in SHA-256 repos.</summary>
public static class ObjectId
{
    public static bool IsValid(string? id) =>
        id is { Length: 40 or 64 } && id.All(char.IsAsciiHexDigitLower);

    /// <summary>All zeros: what git prints for the missing side of an added or deleted file.</summary>
    public static bool IsZero(string id) => id.All(c => c == '0');

    /// <summary>For ids read from git's output.</summary>
    public static string Parse(string id) =>
        IsValid(id) ? id : throw new FormatException($"Expected a full object id, got '{id}'.");

    /// <summary>
    /// For ids that are passed to git. Anything else is refused, so a value from outside can never be read as an
    /// option or a revision expression.
    /// </summary>
    public static string Require(string id) =>
        IsValid(id) ? id : throw new ArgumentException($"Not a full object id: '{id}'.", nameof(id));
}
