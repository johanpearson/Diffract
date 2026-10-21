using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Diffract.Sidecar;

/// <param name="Path">The program's full path. Null when Windows does not say, as for some system processes.</param>
internal sealed record ProcessInfo(int Id, int ParentId, string? Path);

/// <summary>The processes running on this machine. Only reads. Tests describe their own.</summary>
internal interface IProcessTable
{
    IReadOnlyList<ProcessInfo> Snapshot();

    /// <summary>A program's name as people know it, such as "Visual Studio Code" for Code.exe.</summary>
    string Describe(string path);
}

/// <summary>Reads the process list with the Tool Help functions Task Manager also uses. Native AOT friendly.</summary>
internal sealed partial class ProcessTable : IProcessTable
{
    private const uint SnapProcess = 0x2;
    private const uint QueryLimitedInformation = 0x1000;

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        using var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot.IsInvalid)
            return [];

        var processes = new List<ProcessInfo>();
        var entry = new ProcessEntry32 { Size = (uint)Unsafe.SizeOf<ProcessEntry32>() };
        for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            processes.Add(new ProcessInfo((int)entry.ProcessId, (int)entry.ParentProcessId, ImagePath(entry.ProcessId)));
        return processes;
    }

    public string Describe(string path)
    {
        try
        {
            if (FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                return description.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Limited query rights are enough for the path, and Windows grants them for most processes, elevated ones of the
    /// same user included. Null when it does not.
    /// </summary>
    private static string? ImagePath(uint processId)
    {
        using var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            return null;

        Span<ushort> buffer = stackalloc ushort[1024];
        var length = (uint)buffer.Length;
        return QueryFullProcessImageName(process, 0, ref buffer[0], ref length)
            ? new string(MemoryMarshal.Cast<ushort, char>(buffer[..(int)length]))
            : null;
    }

    /// <summary>PROCESSENTRY32W. The name is UTF-16, kept as ushort so the struct stays blittable.</summary>
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public ExeFileName ExeFile;
    }

    [InlineArray(260)]
    private struct ExeFileName
    {
        private ushort _first;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, ref ushort name, ref uint size);
}
