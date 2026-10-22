namespace Diffract.Git.Changes;

/// <summary>
/// The header of one file in a diff. A rename or copy header becomes a plain change of the new path: unstaging one of
/// its hunks must change the content and keep the rename, where git's own header applied in reverse undoes the rename.
/// </summary>
internal static class PatchHeader
{
    private static readonly byte[][] RenameLines =
        [.. new[] { "similarity index ", "dissimilarity index ", "rename from ", "rename to ", "copy from ", "copy to " }.Select(System.Text.Encoding.ASCII.GetBytes)];

    /// <summary>
    /// Without its rename or copy lines, and with the new path on both sides, taken from git's own "+++" line so its
    /// quoting is kept. Any other header is returned as it is.
    /// </summary>
    public static IReadOnlyList<PatchLine> AsChangeOfNewPath(IReadOnlyList<PatchLine> header)
    {
        if (!header.Any(IsRenameLine))
            return header;
        var newPath = header.FirstOrDefault(l => l.StartsWith("+++ "u8));
        if (newPath.Bytes is null)
            return header;

        var name = newPath.Bytes[4..];
        var oldName = AsOldSide(name);
        return [.. header.Where(l => !IsRenameLine(l)).Select(line => Rewrite(line, oldName, name))];
    }

    private static PatchLine Rewrite(PatchLine line, byte[] oldName, byte[] name)
    {
        if (line.StartsWith("diff --git "u8))
            return new PatchLine([.. "diff --git "u8, .. Trim(oldName), .. " "u8, .. Trim(name), .. "\n"u8]);
        return line.StartsWith("--- "u8) ? new PatchLine([.. "--- "u8, .. oldName]) : line;
    }

    private static bool IsRenameLine(PatchLine line) => RenameLines.Any(prefix => line.StartsWith(prefix));

    /// <summary>The name without its line feed, and without the tab git puts after a quoted name.</summary>
    private static byte[] Trim(byte[] name) => [.. name.AsSpan().TrimEnd("\t\n"u8)];

    /// <summary>The first "b/" becomes "a/": the prefix, after the quote of a quoted name.</summary>
    private static byte[] AsOldSide(byte[] name)
    {
        byte[] copy = [.. name];
        "a/"u8.CopyTo(copy.AsSpan(copy.AsSpan().IndexOf("b/"u8)));
        return copy;
    }
}
