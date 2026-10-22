using System.Text;

namespace Diffract.Git.Changes;

/// <summary>One line of a diff as git wrote it, line feed included, every byte kept.</summary>
public readonly record struct PatchLine(byte[] Bytes)
{
    public bool StartsWith(ReadOnlySpan<byte> prefix) => Bytes.AsSpan().StartsWith(prefix);

    public string Text => Encoding.UTF8.GetString(Bytes).TrimEnd('\n');

    /// <summary>Split on line feeds only, so a CR stays where git put it. A last line without one is kept as it is.</summary>
    public static IEnumerable<PatchLine> Split(byte[] bytes)
    {
        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var next = end < 0 ? bytes.Length : end + 1;
            yield return new PatchLine(bytes[start..next]);
            start = next;
        }
    }

    public static byte[] Join(IEnumerable<PatchLine> lines) => [.. lines.SelectMany(l => l.Bytes)];
}
