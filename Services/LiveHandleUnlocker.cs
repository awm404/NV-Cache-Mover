using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NvidiaCacheMover.Services;

/// <summary>
/// Releases live NVIDIA shader cache file locks (.nvph/.nvbin) held by user-session processes
/// without killing applications, requiring Administrator rights, or booting into Safe Mode.
/// Handles all 3 reference layers used by nvwgf2umx.dll:
///   1. Memory-mapped views (MapViewOfFile -> NtUnmapViewOfSection)
///   2. File handles (CreateFileW -> DuplicateHandle DUPLICATE_CLOSE_SOURCE)
///   3. Section handles (CreateFileMappingW -> DuplicateHandle DUPLICATE_CLOSE_SOURCE)
/// </summary>
public static class LiveHandleUnlocker
{
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private const uint DUPLICATE_CLOSE_SOURCE = 0x0001;
    private const uint DUPLICATE_SAME_ACCESS = 0x0002;
    private const uint FILE_MAP_READ = 0x0004;
    private const uint FILE_TYPE_DISK = 0x0001;

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_MAPPED = 0x40000;

    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int FileProcessIdsUsingFileInformation = 47;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryVirtualMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        int memoryInformationClass,
        IntPtr memoryInformation,
        UIntPtr memoryInformationLength,
        out UIntPtr returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtUnmapViewOfSection(IntPtr processHandle, IntPtr baseAddress);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(
        IntPtr fileHandle,
        out IO_STATUS_BLOCK ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        [Out] StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        IntPtr hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, [Out] StringBuilder lpTargetPath, uint ucchMax);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    public static bool IsFileLocked(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static HashSet<int> GetLockingProcessIds(string directoryPath, HashSet<string>? excludedTopSubdirs = null)
    {
        var pids = new HashSet<int>();
        if (!Directory.Exists(directoryPath))
            return pids;

        int selfPid = Environment.ProcessId;
        foreach (string file in EnumerateRealFiles(directoryPath, excludedTopSubdirs))
        {
            if (!IsFileLocked(file))
                continue;

            foreach (int pid in GetFileUserPids(file))
            {
                if (pid > 4 && pid != selfPid)
                    pids.Add(pid);
            }
        }
        return pids;
    }

    public static int UnlockDirectory(
        string physicalDirectoryPath,
        Action<string>? log = null,
        HashSet<string>? excludedTopSubdirs = null)
    {
        if (!Directory.Exists(physicalDirectoryPath))
            return 0;

        string fullPath = Path.GetFullPath(physicalDirectoryPath).TrimEnd('\\') + "\\";
        string? ntPathPrefix = TryConvertDosPathToNtPath(fullPath);
        string dosPrefix = @"\\?\" + fullPath;

        HashSet<int> lockingPids = GetLockingProcessIds(physicalDirectoryPath, excludedTopSubdirs);
        if (lockingPids.Count == 0)
            return 0;

        log?.Invoke($"Found {lockingPids.Count} active process(es) holding shader cache handles under {physicalDirectoryPath}. Releasing locks...");

        bool MatchesTarget(string candidate)
        {
            if (candidate.StartsWith(dosPrefix, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
            {
                if (excludedTopSubdirs is { Count: > 0 })
                {
                    string rel = candidate.StartsWith(dosPrefix, StringComparison.OrdinalIgnoreCase)
                        ? candidate[dosPrefix.Length..]
                        : candidate[fullPath.Length..];
                    int slash = rel.IndexOf('\\');
                    string top = slash >= 0 ? rel[..slash] : rel;
                    if (excludedTopSubdirs.Contains(top))
                        return false;
                }
                return true;
            }

            if (ntPathPrefix != null && candidate.StartsWith(ntPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (excludedTopSubdirs is { Count: > 0 })
                {
                    string rel = candidate[ntPathPrefix.Length..];
                    int slash = rel.IndexOf('\\');
                    string top = slash >= 0 ? rel[..slash] : rel;
                    if (excludedTopSubdirs.Contains(top))
                        return false;
                }
                return true;
            }

            return false;
        }

        var procHandles = new Dictionary<int, IntPtr>();
        IntPtr mbiPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
        IntPtr namePtr = Marshal.AllocHGlobal(2048);
        IntPtr selfHandle = Process.GetCurrentProcess().Handle;
        int releasedCount = 0;

        try
        {
            // Step 1: Unmap memory-mapped views in locking processes
            foreach (int pid in lockingPids)
            {
                IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_DUP_HANDLE, false, (uint)pid);
                if (hProc == IntPtr.Zero)
                    continue;

                procHandles[pid] = hProc;
                long addr = 0;
                while (addr < 0x7FFFFFFF0000L)
                {
                    int st = NtQueryVirtualMemory(
                        hProc,
                        (IntPtr)addr,
                        0,
                        mbiPtr,
                        (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>(),
                        out _);

                    if (st != 0)
                        break;

                    var mbi = Marshal.PtrToStructure<MEMORY_BASIC_INFORMATION>(mbiPtr);
                    if (mbi.State == MEM_COMMIT && mbi.Type == MEM_MAPPED)
                    {
                        if (NtQueryVirtualMemory(hProc, mbi.BaseAddress, 2, namePtr, (UIntPtr)2048, out _) == 0)
                        {
                            var us = Marshal.PtrToStructure<UNICODE_STRING>(namePtr);
                            if (us.Length > 0 && us.Buffer != IntPtr.Zero)
                            {
                                string mappedName = Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                                if (MatchesTarget(mappedName))
                                {
                                    if (NtUnmapViewOfSection(hProc, mbi.BaseAddress) == 0)
                                        releasedCount++;
                                }
                            }
                        }
                    }

                    long next = (long)mbi.BaseAddress + (long)mbi.RegionSize;
                    if (next <= addr)
                        break;
                    addr = next;
                }
            }

            if (procHandles.Count == 0)
                return releasedCount;

            // Step 2: Close File & Section handles in locking processes
            int bufSize = 0x200000;
            IntPtr handleTablePtr = Marshal.AllocHGlobal(bufSize);
            try
            {
                int status;
                while ((status = NtQuerySystemInformation(SystemExtendedHandleInformation, handleTablePtr, bufSize, out int retLen)) == StatusInfoLengthMismatch)
                {
                    Marshal.FreeHGlobal(handleTablePtr);
                    bufSize = retLen + 0x40000;
                    handleTablePtr = Marshal.AllocHGlobal(bufSize);
                }

                if (status == 0)
                {
                    long count = Marshal.ReadInt64(handleTablePtr);
                    int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
                    IntPtr cur = IntPtr.Add(handleTablePtr, IntPtr.Size * 2);
                    var pathBuilder = new StringBuilder(1024);

                    for (long i = 0; i < count; i++)
                    {
                        var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(cur);
                        cur = IntPtr.Add(cur, entrySize);

                        int pid = (int)entry.UniqueProcessId;
                        if (!procHandles.TryGetValue(pid, out IntPtr hProc))
                            continue;

                        // Check File handle
                        if (DuplicateHandle(hProc, (IntPtr)(long)entry.HandleValue, selfHandle, out IntPtr dupFile, 0, false, DUPLICATE_SAME_ACCESS))
                        {
                            bool isTargetFile = false;
                            try
                            {
                                if (GetFileType(dupFile) == FILE_TYPE_DISK)
                                {
                                    pathBuilder.Clear();
                                    if (GetFinalPathNameByHandleW(dupFile, pathBuilder, (uint)pathBuilder.Capacity, 0) > 0)
                                    {
                                        if (MatchesTarget(pathBuilder.ToString()))
                                            isTargetFile = true;
                                    }
                                }
                            }
                            finally
                            {
                                CloseHandle(dupFile);
                            }

                            if (isTargetFile)
                            {
                                if (DuplicateHandle(hProc, (IntPtr)(long)entry.HandleValue, IntPtr.Zero, out _, 0, false, DUPLICATE_CLOSE_SOURCE))
                                    releasedCount++;
                                continue;
                            }
                        }

                        // Check Section handle
                        if (DuplicateHandle(hProc, (IntPtr)(long)entry.HandleValue, selfHandle, out IntPtr dupSec, FILE_MAP_READ, false, 0))
                        {
                            IntPtr tempView = MapViewOfFile(dupSec, FILE_MAP_READ, 0, 0, (UIntPtr)4096);
                            CloseHandle(dupSec);

                            if (tempView != IntPtr.Zero)
                            {
                                bool isTargetSection = false;
                                try
                                {
                                    if (NtQueryVirtualMemory(selfHandle, tempView, 2, namePtr, (UIntPtr)2048, out _) == 0)
                                    {
                                        var us = Marshal.PtrToStructure<UNICODE_STRING>(namePtr);
                                        if (us.Length > 0 && us.Buffer != IntPtr.Zero)
                                        {
                                            string secName = Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                                            if (MatchesTarget(secName))
                                                isTargetSection = true;
                                        }
                                    }
                                }
                                finally
                                {
                                    UnmapViewOfFile(tempView);
                                }

                                if (isTargetSection)
                                {
                                    if (DuplicateHandle(hProc, (IntPtr)(long)entry.HandleValue, IntPtr.Zero, out _, 0, false, DUPLICATE_CLOSE_SOURCE))
                                        releasedCount++;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(handleTablePtr);
            }
        }
        finally
        {
            foreach (IntPtr h in procHandles.Values)
                CloseHandle(h);

            Marshal.FreeHGlobal(mbiPtr);
            Marshal.FreeHGlobal(namePtr);
        }

        log?.Invoke($"Released {releasedCount} handle/view lock(s) across {procHandles.Count} process(es).");
        return releasedCount;
    }

    private static IEnumerable<int> GetFileUserPids(string filePath)
    {
        IntPtr hFile = CreateFileW(filePath, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (hFile == new IntPtr(-1) || hFile == IntPtr.Zero)
            return [];

        IntPtr buf = Marshal.AllocHGlobal(8192);
        try
        {
            int st = NtQueryInformationFile(hFile, out _, buf, 8192, FileProcessIdsUsingFileInformation);
            if (st != 0)
                return [];

            long count = Marshal.ReadInt64(buf);
            if (count <= 0 || count > 512)
                return [];

            var list = new List<int>((int)count);
            for (int i = 0; i < count; i++)
            {
                list.Add((int)Marshal.ReadInt64(buf, 8 + i * 8));
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            CloseHandle(hFile);
        }
    }

    private static string? TryConvertDosPathToNtPath(string fullDosPath)
    {
        if (fullDosPath.Length < 3 || fullDosPath[1] != ':')
            return null;

        string drive = fullDosPath[..2]; // e.g. "D:"
        var sb = new StringBuilder(512);
        if (QueryDosDeviceW(drive, sb, (uint)sb.Capacity) == 0)
            return null;

        string ntDevice = sb.ToString(); // e.g. "\Device\HarddiskVolume4"
        return ntDevice + fullDosPath[2..];
    }

    private static IEnumerable<string> EnumerateRealFiles(string rootDir, HashSet<string>? excludedTopSubdirs)
    {
        var stack = new Stack<(string Path, bool IsRoot)>();
        stack.Push((rootDir, true));

        while (stack.Count > 0)
        {
            var (cur, isRoot) = stack.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(cur);
            }
            catch
            {
                continue;
            }

            foreach (string f in files)
                yield return f;

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(cur);
            }
            catch
            {
                continue;
            }

            foreach (string d in dirs)
            {
                try
                {
                    var di = new DirectoryInfo(d);
                    if (isRoot && excludedTopSubdirs != null && excludedTopSubdirs.Contains(di.Name))
                        continue;
                    if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    stack.Push((d, false));
                }
                catch
                {
                    // Ignore inaccessible subfolders
                }
            }
        }
    }
}
