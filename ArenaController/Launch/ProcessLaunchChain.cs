using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArenaController;

/// <summary>
/// Verifies that an already-running Hearthstone process belongs to the
/// authenticated Battle.net/Box launch chain.  A raw Hearthstone.exe started
/// from Explorer or a shell can look healthy while missing the Battle.net
/// session that recovery needs, so the controller must refuse to attach to it.
/// </summary>
internal static class ProcessLaunchChain
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static readonly HashSet<string> AuthorizedOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "Battle.net.exe",
        "HSAng.exe",
        "HSAExternal.exe",
        "Agent.exe"
    };

    public static bool TryGetAuthorizedOwner(int processId, out string owner, out int ownerPid)
    {
        owner = string.Empty;
        ownerPid = 0;
        if (!TryReadParent(processId, out var parentPid, out var parentName)) return false;
        owner = parentName;
        ownerPid = parentPid;
        return AuthorizedOwners.Contains(parentName);
    }

    private static bool TryReadParent(int processId, out int parentPid, out string parentName)
    {
        parentPid = 0;
        parentName = string.Empty;
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandle) return false;
        try
        {
            var entries = new Dictionary<int, (int ParentPid, string Name)>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return false;
            do
            {
                entries[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile ?? string.Empty);
            } while (Process32NextW(snapshot, ref entry));

            if (!entries.TryGetValue(processId, out var child)) return false;
            parentPid = child.ParentPid;
            if (!entries.TryGetValue(parentPid, out var parent)) return false;
            parentName = parent.Name;
            return !string.IsNullOrWhiteSpace(parentName);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
