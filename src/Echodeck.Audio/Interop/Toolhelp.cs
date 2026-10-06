using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Echodeck.Audio.Interop;

/// <summary>
/// Toolhelp32 process snapshot. Used instead of System.Diagnostics.Process because it returns
/// the parent PID (needed to find the root Discord.exe) for every process in one cheap call,
/// without opening a handle to each process.
/// </summary>
internal static class Toolhelp
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
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
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);

    public static List<(int Pid, int ParentPid, string ExeName)> Snapshot()
    {
        var result = new List<(int, int, string)>(512);
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed");

        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snapshot, ref entry)) return result;
        do
        {
            result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty));
        }
        while (Process32NextW(snapshot, ref entry));
        return result;
    }
}
