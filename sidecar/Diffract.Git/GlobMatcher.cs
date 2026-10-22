namespace Diffract.Git;

/// <summary>
/// Matches paths and ref names against glob patterns. <c>*</c> and <c>?</c> stay within one
/// segment, <c>**</c> spans any number of segments (including none). Both / and \ separate segments.
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string path, bool ignoreCase) =>
        MatchSegments(Split(pattern), Split(path), ignoreCase);

    public static bool IsMatchAny(IEnumerable<string> patterns, string path, bool ignoreCase) =>
        patterns.Any(p => IsMatch(p, path, ignoreCase));

    private static string[] Split(string value) =>
        value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchSegments(ReadOnlySpan<string> pattern, ReadOnlySpan<string> path, bool ignoreCase)
    {
        if (pattern.IsEmpty)
            return path.IsEmpty;

        if (pattern[0] == "**")
        {
            for (var skip = 0; skip <= path.Length; skip++)
            {
                if (MatchSegments(pattern[1..], path[skip..], ignoreCase))
                    return true;
            }
            return false;
        }

        return !path.IsEmpty
            && MatchSegment(pattern[0], path[0], ignoreCase)
            && MatchSegments(pattern[1..], path[1..], ignoreCase);
    }

    // Classic wildcard match with backtracking to the last '*'.
    private static bool MatchSegment(string pattern, string text, bool ignoreCase)
    {
        int p = 0, t = 0, star = -1, starText = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && CharMatches(pattern[p], text[t], ignoreCase))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starText = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        // Whatever is left of the pattern must be stars.
        return pattern.AsSpan(p).TrimStart('*').IsEmpty;
    }

    private static bool CharMatches(char pattern, char text, bool ignoreCase) =>
        pattern == '?' || pattern == text || (ignoreCase && char.ToUpperInvariant(pattern) == char.ToUpperInvariant(text));
}
