using System.Runtime.InteropServices;

namespace KernelProbe;

internal static unsafe class Native
{
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    public const uint PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04;
    public const uint FILE_MAP_READ = 0x0004, FILE_MAP_WRITE = 0x0002;

    // SetProcessWorkingSetSizeEx flags
    public const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    public const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;
    public const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4;
    public const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void* VirtualAlloc(void* addr, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFree(void* addr, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualUnlock(void* addr, nuint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attrs, uint protect, uint maxHigh, uint maxLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void* MapViewOfFile(IntPtr hMap, uint access, uint offHigh, uint offLow, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UnmapViewOfFile(void* addr);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessWorkingSetSizeEx(IntPtr h, nuint min, nuint max, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out nuint min, out nuint max, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32QueryWorkingSetEx(IntPtr h, void* buffer, uint cb);

    // ProcessMemoryPriority = 0, MEMORY_PRIORITY_INFORMATION { ULONG MemoryPriority; }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr h, int infoClass, void* info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, void* info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    // PDH
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetRawCounterValue(IntPtr counter, out uint type, out PdhRawCounter value);

    [StructLayout(LayoutKind.Sequential)]
    public struct PdhRawCounter
    {
        public uint CStatus;
        public uint TimeLow, TimeHigh;
        public long FirstValue;
        public long SecondValue;
        public uint MultiCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WsExEntry
    {
        public IntPtr VirtualAddress;
        public ulong Attributes;
    }
}
