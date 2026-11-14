using System.Runtime.InteropServices;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Test-only process-tree snapshot (Toolhelp32), used to scope "did WE leak a child?" checks to
/// processes this test host actually started. Public-release audit: the previous orphan sweep
/// compared every cmd.exe on the whole machine before/after, so any unrelated cmd.exe started by
/// another application during the test window failed it (observed: two foreign cmd processes).
/// </summary>
internal static class ProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    internal readonly record struct Entry(int Pid, int ParentPid, string ExeName);

    /// <summary>Every process currently running, with its parent PID.</summary>
    internal static IReadOnlyList<Entry> Snapshot()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandle)
            throw new InvalidOperationException("CreateToolhelp32Snapshot failed: " + Marshal.GetLastWin32Error());

        try
        {
            var list = new List<Entry>();
            var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return list;
            do
            {
                list.Add(new Entry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
            return list;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>PIDs of the running direct children of <paramref name="parentPid"/> whose image
    /// name is <paramref name="exeName"/> (case-insensitive, e.g. "cmd.exe").</summary>
    internal static HashSet<int> ChildProcessIds(int parentPid, string exeName)
        => Snapshot()
            .Where(p => p.ParentPid == parentPid &&
                        string.Equals(p.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid)
            .ToHashSet();
}
