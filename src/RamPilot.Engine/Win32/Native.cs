using System.Runtime.InteropServices;

namespace RamPilot.Win32;

/// <summary>All P/Invoke declarations of the engine. Blittable signatures only (Native AOT friendly).</summary>
internal static unsafe class Native
{
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x0010_0000;

    public const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    public const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;
    public const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4;
    public const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

    public const uint STILL_ACTIVE = 259;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint INFINITE = 0xFFFF_FFFF;

    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

    // ---- processes ----
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint access, int inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CloseHandle(nint h);

    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GetProcessWorkingSetSizeEx(nint h, out nuint min, out nuint max, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int SetProcessWorkingSetSizeEx(nint h, nuint min, nuint max, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GetExitCodeProcess(nint h, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int QueryProcessCycleTime(nint h, out ulong cycles);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int QueryFullProcessImageNameW(nint h, uint flags, char* buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(nint h, uint ms);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetProcessMemoryInfo")]
    public static extern int GetProcessMemoryInfo(nint h, out PROCESS_MEMORY_COUNTERS_EX c, uint cb);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, void* buf, uint len, out uint retLen);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(nint h, int cls, void* buf, uint len, out uint retLen);

    // ---- memory ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public const int LowMemoryResourceNotification = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint CreateMemoryResourceNotification(int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int QueryMemoryResourceNotification(nint h, out int state);

    // ---- windows / focus ----
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> proc, nint lParam);

    [DllImport("user32.dll")]
    public static extern int ShowWindow(nint h, int cmd);

    [DllImport("user32.dll")]
    public static extern int SetForegroundWindow(nint h);

    // ---- console / locale ----
    [DllImport("kernel32.dll")]
    public static extern int AttachConsole(int pid);

    [DllImport("kernel32.dll")]
    public static extern ushort GetUserDefaultUILanguage();

    // ---- COM ----
    public const uint COINIT_MULTITHREADED = 0x0;
    public const uint CLSCTX_ALL = 0x17;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(nint reserved, uint coinit);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint ppv);

    public static void Close(nint h)
    {
        if (h != 0) _ = CloseHandle(h);
    }

    /// <summary>Full image path of a process handle, or null.</summary>
    public static string? ImagePath(nint h)
    {
        char* buf = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageNameW(h, 0, buf, ref size) != 0 ? new string(buf, 0, (int)size) : null;
    }
}
