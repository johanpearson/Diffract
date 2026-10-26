using System.Runtime.InteropServices;

namespace Diffract.Leftovers.Cleanup;

/// <summary>What a cleanup does to folders outside git. Tests replace it to open a file at the worst moment.</summary>
public interface IFolderOperations
{
    /// <summary>Why a program may be using files in the folder, or null when none is and the folder is there.</summary>
    string? InUse(string path);

    /// <summary>Moves the folder to the Recycle Bin. Null when it did, or why not.</summary>
    string? Recycle(string path);
}

/// <summary>
/// Windows refuses to rename a folder while any file in it is open, whatever the sharing, so renaming it away and back
/// is the check (docs/findings/cleanup.md). The Recycle Bin is the shell's, as Explorer uses it.
/// </summary>
public sealed partial class WindowsFolders : IFolderOperations
{
    public static WindowsFolders Instance { get; } = new();

    private const uint Delete = 3;
    private const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400, WantNukeWarning = 0x4000;

    public string? InUse(string path)
    {
        if (!Directory.Exists(path))
            return $"The folder {path} is gone.";
        var aside = $"{Path.TrimEndingDirectorySeparator(path)}.diffract-check-{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            Directory.Move(path, aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"A program is using files in {path}.";
        }

        return MoveBack(aside, path);
    }

    /// <summary>
    /// Windows asks before deleting for good instead, as when the folder is too big for the Recycle Bin, so nothing is
    /// ever lost without the user saying so. A drive with no Recycle Bin at all, as a network drive, is left alone.
    /// </summary>
    public string? Recycle(string path)
    {
        var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        if (Path.GetPathRoot(path) is not { Length: > 0 } root || SHQueryRecycleBin(root, ref info) != 0)
            return $"The drive of {path} has no Recycle Bin, so the folder was left as it is.";

        var from = Marshal.StringToHGlobalUni(path + '\0');
        try
        {
            var operation = new FileOperation { Function = Delete, From = from, Flags = AllowUndo | NoConfirmation | NoErrorUi | Silent | WantNukeWarning };
            var error = SHFileOperation(ref operation);
            return error == 0 && operation.AnyAborted == 0 && !Directory.Exists(path)
                ? null
                : $"Windows did not move {path} to the Recycle Bin (error {error}). A program may be using files in it.";
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    /// <summary>No program knows the folder's new name yet, so nothing should hold it. A few quick tries are enough.</summary>
    private static string? MoveBack(string aside, string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Move(aside, path);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Try again.
            }
        }

        return $"Diffract renamed {path} to {aside} to check that no program uses it, and could not rename it back. Rename it back by hand.";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public uint Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref FileOperation operation);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string rootPath, ref RecycleBinInfo info);
}
