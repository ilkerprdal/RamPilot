namespace RamPilot.Win32;

/// <summary>
/// An open process handle bound to one process instance (pid + create time), with the working-set operations
/// RamPilot uses. Keeping the handle open protects against PID reuse.
/// </summary>
internal sealed unsafe class ProcessHandle : IDisposable
{
    private const int ProcessCommandLineInformation = 60;
    private const int MaxCommandLineBytes = 1 << 20;

    private nint _h;

    public uint Pid { get; }
    public long CreateTime { get; }
    public nuint OriginalMin { get; }
    public nuint OriginalMax { get; }

    private ProcessHandle(nint h, uint pid, long create, nuint min, nuint max)
    {
        _h = h; Pid = pid; CreateTime = create; OriginalMin = min; OriginalMax = max;
    }

    /// <summary>Opens the process if it still is the instance created at <paramref name="createTime"/>.</summary>
    public static ProcessHandle? Open(uint pid, long createTime)
    {
        nint h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (h == 0) return null;
        if (Native.GetProcessTimes(h, out long creation, out _, out _, out _) == 0 || creation != createTime
            || Native.GetProcessWorkingSetSizeEx(h, out var min, out var max, out _) == 0)
        {
            Native.Close(h);
            return null;
        }
        return new ProcessHandle(h, pid, createTime, min, max);
    }

    public bool IsAlive => _h != 0 && Native.GetExitCodeProcess(_h, out uint c) != 0 && c == Native.STILL_ACTIVE;

    public string? ImagePath() => _h == 0 ? null : Native.ImagePath(_h);

    /// <summary>
    /// Reads the command line with a growable buffer (Chromium command lines often exceed 8 KB).
    /// Returns null if it cannot be read; callers must then fail closed.
    /// </summary>
    public string? ReadCommandLine()
    {
        if (_h == 0) return null;
        uint size = 8192;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buf = new byte[size];
            fixed (byte* p = buf)
            {
                int st = Native.NtQueryInformationProcess(_h, ProcessCommandLineInformation, p, size, out uint ret);
                if (st is Native.STATUS_INFO_LENGTH_MISMATCH or Native.STATUS_BUFFER_OVERFLOW or Native.STATUS_BUFFER_TOO_SMALL)
                {
                    uint want = ret > size ? ret + 64 : size * 2;
                    if (want > MaxCommandLineBytes) return null;
                    size = want;
                    continue;
                }
                if (st < 0) return null;
                ushort len = *(ushort*)p;
                byte* s = *(byte**)(p + 8);
                if (s == null || len == 0 || (len & 1) != 0) return null;
                if (s < p || s + len > p + size) return null;
                return new string((char*)s, 0, len / 2);
            }
        }
        return null;
    }

    /// <summary>True if our hard maximum is in force (the target may clear it with its own SetProcessWorkingSetSize).</summary>
    public ulong CurrentMax() =>
        _h != 0 && Native.GetProcessWorkingSetSizeEx(_h, out _, out var max, out _) != 0 ? (ulong)max : 0;

    public bool HardMaxActive() =>
        _h != 0 && Native.GetProcessWorkingSetSizeEx(_h, out _, out _, out uint f) != 0
        && (f & Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0;

    /// <summary>Hard working-set maximum. The kernel's working-set manager picks which pages leave (aging).</summary>
    public bool SetHardMax(ulong bytes)
    {
        if (_h == 0) return false;
        nuint min = (nuint)Math.Min((ulong)OriginalMin, bytes / 2);
        return Native.SetProcessWorkingSetSizeEx(_h, min, (nuint)bytes,
            Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE | Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0;
    }

    /// <summary>
    /// Removes the hard maximum. Experiment finding (experiments/README.md, phase 0 and probe C): a call with a small soft
    /// maximum trims immediately, so the soft maximum is set to max(original, 2 x current WS, 64 MB).
    /// </summary>
    public bool ClearHardMax(ulong currentWs) => ClearHardMax(_h, OriginalMin, OriginalMax, currentWs);

    public static bool ClearHardMax(nint h, nuint min, nuint max, ulong currentWs)
    {
        if (h == 0) return false;
        ulong safeMax = Math.Max((ulong)max, Math.Max(currentWs * 2, 64UL << 20));
        nuint safeMin = (nuint)Math.Min((ulong)min, safeMax / 2);
        return Native.SetProcessWorkingSetSizeEx(h, safeMin, (nuint)safeMax,
            Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE | Native.QUOTA_LIMITS_HARDWS_MAX_DISABLE) != 0;
    }

    public void Dispose()
    {
        Native.Close(_h);
        _h = 0;
    }
}
