namespace Diffract.Git;

/// <summary>
/// Matches paths and ref names against glob patterns. <c>*</c> and <c>?</c> stay within one
/// segment, <c>**</c> spans any number of segments (including none). Both / and \ separate segments.
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string path, bool ignoreCase) =>
        MatchSegments(Split(pattern), 0, Split(path), 0, ignoreCase);

    public static bool IsMatchAny(IEnumerable<string> patterns, string path, bool ignoreCase) =>
        patterns.Any(p => IsMatch(p, path, ignoreCase));

    private static string[] Split(string value) =>
        value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchSegments(string[] pattern, int p, string[] path, int s, bool ignoreCase)
    {
        if (p == pattern.Length)
            return s == path.Length;

        if (pattern[p] == "**")
        {
            for (var skip = s; skip <= path.Length; skip++)
            {
                if (MatchSegments(pattern, p + 1, path, skip, ignoreCase))
                    return true;
            }
            return false;
        }

        return s < path.Length
            && MatchSegment(pattern[p], path[s], ignoreCase)
            && MatchSegments(pattern, p + 1, path, s + 1, ignoreCase);
    }

    // Classic wildcard match with backtracking to the last '*'.
    private static bool MatchSegment(string pattern, string text, bool ignoreCase)
    {
        int p = 0, t = 0, star = -1, starText = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], text[t], ignoreCase)))
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

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static bool CharEquals(char a, char b, bool ignoreCase) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
}
