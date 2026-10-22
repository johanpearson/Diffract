using System.Text;

namespace Diffract.Git.Files;

public enum FileContentKind { Text, Binary, TooLarge }

/// <summary>One side of a file's diff.</summary>
/// <param name="Text">The content when <see cref="Kind"/> is Text, decoded as UTF-8 without a byte order mark. Null otherwise.</param>
public sealed record FileContent(FileContentKind Kind, string? Text);

/// <summary>
/// Reads one side of a diff from a stream, and stops as soon as it is clearly binary or larger than the limit, so a
/// large file is never held whole. Its <see cref="ReadAsync"/> fits <see cref="GitOutputReader"/>.
/// </summary>
public sealed class FileContentReader(int maxBytes) : IDisposable
{
    /// <summary>Larger files are refused rather than sent whole.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>As git: a file is binary when its first 8000 bytes hold a NUL.</summary>
    private const int BinaryCheckBytes = 8000;

    private readonly MemoryStream _buffer = new();
    private FileContentKind? _stoppedAs;

    public FileContentReader() : this(MaxFileBytes)
    {
    }

    /// <summary>What was read. Call it after <see cref="ReadAsync"/>.</summary>
    public FileContent Content => _stoppedAs is { } kind ? new FileContent(kind, null) : new FileContent(FileContentKind.Text, Decode());

    /// <returns>True when it read to the end, false when it stopped early.</returns>
    public async Task<bool> ReadAsync(Stream input, CancellationToken ct)
    {
        var chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            var checkedBefore = (int)Math.Min(_buffer.Length, BinaryCheckBytes);
            _buffer.Write(chunk, 0, read);
            _stoppedAs = Classify(checkedBefore);
            if (_stoppedAs is not null)
                return false;
        }
        return true;
    }

    /// <summary>Stops a file known to be larger than the limit before reading any of it.</summary>
    public FileContent TooLarge()
    {
        _stoppedAs = FileContentKind.TooLarge;
        return Content;
    }

    public bool IsTooLarge(long length) => length > maxBytes;

    public void Dispose() => _buffer.Dispose();

    private FileContentKind? Classify(int checkedBefore)
    {
        if (checkedBefore < BinaryCheckBytes && HasNul(checkedBefore))
            return FileContentKind.Binary;
        return IsTooLarge(_buffer.Length) ? FileContentKind.TooLarge : null;
    }

    private bool HasNul(int from)
    {
        var end = (int)Math.Min(_buffer.Length, BinaryCheckBytes);
        return _buffer.GetBuffer().AsSpan(from, end - from).Contains((byte)0);
    }

    private string Decode()
    {
        var bytes = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        return Encoding.UTF8.GetString(bytes);
    }
}
