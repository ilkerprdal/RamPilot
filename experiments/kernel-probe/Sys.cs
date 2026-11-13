using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace KernelProbe;

/// <summary>One process entry from NtQuerySystemInformation(SystemProcessInformation), x64 layout.</summary>
internal readonly record struct ProcInfo(
    int Pid, string Name, long WorkingSet, long PrivateWs, uint PageFaults, uint HardFaults, ulong Cycles, long KernelTime100ns);

internal static unsafe class Sys
{
    public const long MB = 1024 * 1024;
    public const int Page = 4096;

    // ---------------- NtQuerySystemInformation(SystemProcessInformation = 5) ----------------
    // x64 offsets: NextEntryOffset @0, WorkingSetPrivateSize @8, HardFaultCount @16,
    // CycleTime @24, KernelTime @48, ImageName (UNICODE_STRING) @56, UniqueProcessId @80,
    // PageFaultCount @128, WorkingSetSize @144.
    public static List<ProcInfo> Processes()
    {
        uint size = 1 << 20;
        while (true)
        {
            byte* buf = (byte*)NativeMemory.Alloc(size);
            try
            {
                int st = Native.NtQuerySystemInformation(5, buf, size, out uint ret);
                if (st == unchecked((int)0xC0000004)) { size = Math.Max(size * 2, ret + 65536); continue; }
                if (st != 0) throw new InvalidOperationException($"NtQuerySystemInformation 0x{st:X8}");
                var list = new List<ProcInfo>(400);
                uint off = 0;
                while (true)
                {
                    byte* e = buf + off;
                    ushort nameLen = *(ushort*)(e + 56);
                    char* namePtr = *(char**)(e + 64);
                    string name = nameLen > 0 && namePtr != null ? new string(namePtr, 0, nameLen / 2) : "";
                    list.Add(new ProcInfo(
                        Pid: (int)*(long*)(e + 80),
                        Name: name,
                        WorkingSet: (long)*(ulong*)(e + 144),
                        PrivateWs: *(long*)(e + 8),
                        PageFaults: *(uint*)(e + 128),
                        HardFaults: *(uint*)(e + 16),
                        Cycles: *(ulong*)(e + 24),
                        KernelTime100ns: *(long*)(e + 48)));
                    uint next = *(uint*)e;
                    if (next == 0) break;
                    off += next;
                }
                return list;
            }
            finally { NativeMemory.Free(buf); }
        }
    }

    public static ProcInfo Proc(int pid) => Processes().First(p => p.Pid == pid);

    public static (ProcInfo target, ProcInfo store) ProcAndStore(int pid)
    {
        var all = Processes();
        var t = all.First(p => p.Pid == pid);
        var s = all.FirstOrDefault(p => p.Name == "Memory Compression");
        return (t, s);
    }

    // ---------------- QueryWorkingSetEx ----------------
    /// <summary>Returns the raw PSAPI_WORKING_SET_EX_BLOCK for each page of [baseAddr, +pages).</summary>
    public static ulong[] WsEx(IntPtr process, IntPtr baseAddr, long pages)
    {
        var entries = new Native.WsExEntry[pages];
        for (long i = 0; i < pages; i++) entries[i].VirtualAddress = baseAddr + (nint)(i * Page);
        fixed (Native.WsExEntry* p = entries)
        {
            if (!Native.K32QueryWorkingSetEx(process, p, (uint)(pages * sizeof(Native.WsExEntry))))
                throw new InvalidOperationException("QueryWorkingSetEx failed " + Marshal.GetLastWin32Error());
        }
        var r = new ulong[pages];
        for (long i = 0; i < pages; i++) r[i] = entries[i].Attributes;
        return r;
    }

    public static bool Valid(ulong attr) => (attr & 1) != 0;
    public static int Priority(ulong attr) => (int)((attr >> 24) & 7);
    public static bool Shared(ulong attr) => (attr & (1UL << 15)) != 0;

    // ---------------- Working-set limits ----------------
    public static void SetWs(IntPtr h, long min, long max, uint flags)
    {
        if (!Native.SetProcessWorkingSetSizeEx(h, (nuint)min, (nuint)max, flags))
            throw new InvalidOperationException("SetProcessWorkingSetSizeEx failed " + Marshal.GetLastWin32Error());
    }

    public static string GetWs(IntPtr h)
    {
        Native.GetProcessWorkingSetSizeEx(h, out nuint min, out nuint max, out uint flags);
        return $"min={(ulong)min / 1024}KB max={(ulong)max / 1024}KB flags=0x{flags:X}";
    }

    // ---------------- Job object: children die with the controller ----------------
    private static IntPtr _job;

    public static void AddToKillJob(Process p)
    {
        if (_job == IntPtr.Zero)
        {
            _job = Native.CreateJobObjectW(IntPtr.Zero, null);
            byte* info = stackalloc byte[144];   // JOBOBJECT_EXTENDED_LIMIT_INFORMATION (x64)
            new Span<byte>(info, 144).Clear();
            *(uint*)(info + 16) = 0x2000;          // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!Native.SetInformationJobObject(_job, 9, info, 144))
                throw new InvalidOperationException("SetInformationJobObject " + Marshal.GetLastWin32Error());
        }
        if (!Native.AssignProcessToJobObject(_job, p.Handle))
            throw new InvalidOperationException("AssignProcessToJobObject " + Marshal.GetLastWin32Error());
    }

    // ---------------- Child process helper ----------------
    public static string SelfExe => Environment.ProcessPath!;

    public static string Fmt(double v, int d = 2) => v.ToString("F" + d, CultureInfo.InvariantCulture);
}

/// <summary>System-wide memory counters via PDH (raw = cumulative counts for the "/sec" counters).</summary>
internal sealed class Pdh
{
    public static readonly string[] Paths =
    [
        @"\Memory\Page Faults/sec",
        @"\Memory\Transition Faults/sec",
        @"\Memory\Demand Zero Faults/sec",
        @"\Memory\Cache Faults/sec",
        @"\Memory\Page Reads/sec",
        @"\Memory\Pages Input/sec",
        @"\Memory\Page Writes/sec",
        @"\Memory\Pages Output/sec",
        @"\Memory\Modified Page List Bytes",
        @"\Memory\Standby Cache Reserve Bytes",
        @"\Memory\Standby Cache Normal Priority Bytes",
        @"\Memory\Standby Cache Core Bytes",
        @"\Memory\Free & Zero Page List Bytes",
        @"\Memory\Available Bytes",
    ];

    public static readonly string[] Short =
    [
        "sysPF", "sysTransition", "sysDemandZero", "sysCache", "sysPageReads", "sysPagesInput",
        "sysPageWrites", "sysPagesOutput", "modifiedB", "standbyReserveB", "standbyNormalB", "standbyCoreB",
        "freeZeroB", "availableB",
    ];

    private readonly IntPtr _q;
    private readonly IntPtr[] _c;

    public Pdh()
    {
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) throw new InvalidOperationException("PdhOpenQuery");
        _c = new IntPtr[Paths.Length];
        for (int i = 0; i < Paths.Length; i++)
        {
            uint st = Native.PdhAddEnglishCounterW(_q, Paths[i], IntPtr.Zero, out _c[i]);
            if (st != 0) throw new InvalidOperationException($"PdhAddEnglishCounter {Paths[i]} 0x{st:X}");
        }
    }

    public long[] Read()
    {
        Native.PdhCollectQueryData(_q);
        var v = new long[_c.Length];
        for (int i = 0; i < _c.Length; i++)
        {
            Native.PdhGetRawCounterValue(_c[i], out _, out var raw);
            v[i] = raw.FirstValue;
        }
        return v;
    }

    public static long Get(long[] v, string shortName) => v[Array.IndexOf(Short, shortName)];
}

/// <summary>Controller-side handle to a worker child process speaking a line protocol on stdin/stdout.</summary>
internal sealed class WorkerProc : IDisposable
{
    public readonly Process P;
    public readonly Dictionary<string, (IntPtr Base, long Pages)> Regions = new();

    public WorkerProc(string args)
    {
        var psi = new ProcessStartInfo(Sys.SelfExe, "worker " + args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        P = Process.Start(psi)!;
        Sys.AddToKillJob(P);
        string l;
        while ((l = Expect()) != "READY")
        {
            if (l.StartsWith("REGION ")) AddRegion(l);
        }
    }

    public int Pid => P.Id;
    public IntPtr H => P.Handle;

    private void AddRegion(string l)
    {
        var f = l.Split(' ');
        Regions[f[1]] = ((IntPtr)long.Parse(f[2][2..], NumberStyles.HexNumber), long.Parse(f[3]));
    }

    private string Expect()
    {
        string? l = P.StandardOutput.ReadLine();
        if (l == null) throw new InvalidOperationException("worker exited");
        if (l.StartsWith("ERR")) throw new InvalidOperationException("worker: " + l);
        return l;
    }

    /// <summary>Sends a command and returns the single reply line.</summary>
    public string Cmd(string cmd)
    {
        P.StandardInput.WriteLine(cmd);
        P.StandardInput.Flush();
        string l = Expect();
        if (l.StartsWith("REGION ")) AddRegion(l);
        return l;
    }

    public void Dispose()
    {
        try
        {
            if (!P.HasExited)
            {
                // Clear any hard limit before the process goes away (hygiene; the process exits anyway).
                try { Sys.SetWs(P.Handle, 1 * Sys.MB, 2048 * Sys.MB, Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE | Native.QUOTA_LIMITS_HARDWS_MAX_DISABLE); } catch { }
                try { P.StandardInput.WriteLine("quit"); P.StandardInput.Flush(); } catch { }
                if (!P.WaitForExit(5000)) P.Kill();
            }
        }
        finally { P.Dispose(); }
    }
}

internal sealed class Csv : IDisposable
{
    private readonly StreamWriter _w;
    public readonly string Path;

    public Csv(string path, params string[] header)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _w = new StreamWriter(path, false, new UTF8Encoding(false));
        _w.WriteLine(string.Join(",", header));
    }

    public void Row(params object[] cells)
    {
        _w.WriteLine(string.Join(",", cells.Select(c => c switch
        {
            double d => d.ToString("G6", CultureInfo.InvariantCulture),
            float f => f.ToString("G6", CultureInfo.InvariantCulture),
            string s when s.Contains(',') => "\"" + s + "\"",
            _ => Convert.ToString(c, CultureInfo.InvariantCulture) ?? "",
        })));
        _w.Flush();
    }

    public void Dispose() => _w.Dispose();
}
