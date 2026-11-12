using System.Security.Cryptography;

namespace Diffract.Leftovers.Cleanup;

/// <summary>
/// The copies of files made before a discard, as plain bytes in a folder of Diffract's own under the Windows temp
/// folder: never in the repo, so git sees no tag, ref or object. They last until Diffract closes (docs/findings/discard.md).
/// </summary>
public sealed class DiscardBackups(string folder)
{
    public static DiscardBackups Default { get; } = new(Path.Combine(Path.GetTempPath(), "Diffract", "discards"));

    public string Folder { get; } = folder;

    /// <summary>Keeps the bytes and answers with the name to ask for them by.</summary>
    public string Save(byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
        var name = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(Folder, name), bytes);
        return name;
    }

    /// <summary>The copy, or null when it is gone, as after Diffract was closed. A name that is not one of ours is never read.</summary>
    public byte[]? Read(string name) => PathOf(name) is { } path && File.Exists(path) ? File.ReadAllBytes(path) : null;

    public void Delete(string name)
    {
        if (PathOf(name) is { } path)
            File.Delete(path);
    }

    /// <summary>Removes every copy. Run when Diffract starts and when it exits.</summary>
    public void Clear()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);
    }

    private string? PathOf(string name) => name.Length == 32 && name.All(char.IsAsciiHexDigitLower) ? Path.Combine(Folder, name) : null;
}

/// <summary>A file as it is on disk, compared by its bytes.</summary>
internal static class FileState
{
    /// <summary>The file's bytes, or null when there is no file. Read while editors and agents may have it open.</summary>
    public static byte[]? Read(string fullPath)
    {
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 of the file, or null when there is no file.</summary>
    public static string? HashOf(string fullPath) => Read(fullPath) is { } bytes ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null;
}
