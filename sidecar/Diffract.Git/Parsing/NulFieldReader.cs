using System.Text;

namespace Diffract.Git.Parsing;

/// <summary>
/// Splits git's <c>-z</c> output into NUL-terminated fields while it streams, so a field is ready as soon as its NUL
/// arrives. A UTF-8 character split across two reads is decoded whole, because only complete fields are decoded.
/// </summary>
public sealed class NulFieldReader(Stream stream, int bufferSize = 64 * 1024)
{
    private byte[] _buffer = new byte[bufferSize];
    private int _start;
    private int _end;
    private bool _ended;

    /// <summary>
    /// The next field without its NUL, or null at the end of the stream. Text after the last NUL is returned as a
    /// last field, so a cut-off record shows up as one instead of disappearing.
    /// </summary>
    public async ValueTask<string?> ReadAsync(CancellationToken ct)
    {
        var searched = 0;
        int nul;
        while ((nul = Array.IndexOf(_buffer, (byte)0, _start + searched, _end - _start - searched)) < 0)
        {
            if (_ended)
                return TakeRest();
            searched = _end - _start;
            await FillAsync(ct).ConfigureAwait(false);
        }
        return Take(nul - _start, skip: 1);
    }

    /// <summary>Whatever is left after the last NUL, or null when nothing is.</summary>
    private string? TakeRest() => _start == _end ? null : Take(_end - _start, skip: 0);

    /// <summary>Decodes the next <paramref name="length"/> bytes and moves past them and <paramref name="skip"/> more.</summary>
    private string Take(int length, int skip)
    {
        var field = Encoding.UTF8.GetString(_buffer, _start, length);
        _start += length + skip;
        return field;
    }

    /// <summary>Reads more of the stream into the buffer. Notes when the stream has ended.</summary>
    private async ValueTask FillAsync(CancellationToken ct)
    {
        MakeRoom();
        var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        _ended = read == 0;
        _end += read;
    }

    // Moves the unread part to the front, and grows the buffer when one field fills it.
    private void MakeRoom()
    {
        var unread = _end - _start;
        if (unread == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);
        else if (_start > 0)
            Array.Copy(_buffer, _start, _buffer, 0, unread);
        _start = 0;
        _end = unread;
    }
}
