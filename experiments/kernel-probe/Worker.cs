using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KernelProbe;

/// <summary>
/// Synthetic target process. Owns memory regions, optionally keeps a "hot" subset of the main region
/// touched at a fixed interval, and answers line commands on stdin (see Program.cs usage).
/// </summary>
internal static unsafe class Worker
{
    private sealed class Region
    {
        public required string Name;
        public required string Kind;
        public byte* Base;
        public long Pages;
        public IntPtr Map;
        public FileStream? File;
        public string? FilePath;
    }

    private static readonly Dictionary<string, Region> Regions = new();
    private static volatile bool _paused;
    private static long _sink;
    private static volatile bool _stop;
    private static bool _reverseFill;   // --fill reverse: first-touch pages from the highest address down
    private static Thread? _hot;
    private static string _tmpDir = "";

    public static int Run(Args a)
    {
        _tmpDir = a.Str("tmp", Path.Combine(AppContext.BaseDirectory, "tmp"));
        int memprio = a.Int("memprio", -1);
        if (memprio >= 0)
        {
            uint mp = (uint)memprio;
            if (!Native.SetProcessInformation(Native.GetCurrentProcess(), 0, &mp, 4))
                Console.WriteLine("WARN memprio " + Marshal.GetLastWin32Error());
        }

        long mb = a.Int("mb", 0);
        if (mb > 0)
        {
            _reverseFill = a.Str("fill", "forward") == "reverse";
            var r = Alloc("main", a.Str("kind", "private"), mb, fill: true);
            Console.WriteLine($"REGION main 0x{(long)r.Base:X} {r.Pages}");
            long hotMb = a.Int("hot-mb", 0);
            if (hotMb > 0)
            {
                long[] hot = HotPages(r.Pages, hotMb * Sys.MB / Sys.Page, a.Str("layout", "stride"));
                int interval = a.Int("interval-ms", 100);
                _hot = new Thread(() => HotLoop(r.Base, hot, interval)) { IsBackground = true, Name = "hot" };
                _hot.Start();
            }
        }
        Console.WriteLine("READY");
        Console.Out.Flush();

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            var f = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0) continue;
            try
            {
                switch (f[0])
                {
                    case "quit": Cleanup(); return 0;
                    case "pause": _paused = true; Thread.Sleep(5); Reply("OK pause"); break;
                    case "resume": _paused = false; Reply("OK resume"); break;
                    case "alloc":
                    case "allocz":
                        {
                            var r = Alloc(f[1], f[0] == "allocz" ? "private" : f[2], long.Parse(f[^1]), fill: f[0] == "alloc");
                            Reply($"REGION {r.Name} 0x{(long)r.Base:X} {r.Pages}");
                            break;
                        }
                    case "trim":
                        {
                            var r = Regions[f[1]];
                            // VirtualUnlock on unlocked pages removes them from the working set (documented).
                            Native.VirtualUnlock(r.Base, (nuint)(r.Pages * Sys.Page));
                            Reply("OK trim");
                            break;
                        }
                    case "touch":
                    case "touchw":
                        {
                            var r = Regions[f[1]];
                            var (us, hist) = Touch(r, f[0] == "touchw");
                            Reply($"TOUCH {r.Name} {r.Pages} {us} {string.Join(',', hist)}");
                            break;
                        }
                    case "free": Free(Regions[f[1]]); Regions.Remove(f[1]); Reply("OK free"); break;
                    default: Reply("ERR unknown " + f[0]); break;
                }
            }
            catch (Exception ex) { Reply("ERR " + ex.Message.Replace('\n', ' ')); }
        }
        Cleanup();
        return 0;
    }

    private static void Reply(string s) { Console.WriteLine(s); Console.Out.Flush(); }

    public static long[] HotPages(long pages, long hotPages, string layout)
    {
        if (layout == "block")
        {
            long start = pages / 2 - hotPages / 2;
            return Enumerable.Range(0, (int)hotPages).Select(i => start + i).ToArray();
        }
        long stride = pages / hotPages;   // "stride": every Nth page is hot, interleaved with cold ones
        return Enumerable.Range(0, (int)hotPages).Select(i => i * stride).ToArray();
    }

    private static void HotLoop(byte* b, long[] hot, int intervalMs)
    {
        while (!_stop)
        {
            if (!_paused)
            {
                long s = 0;
                foreach (long p in hot) s += b[p * Sys.Page + 64];
                Interlocked.Add(ref _sink, s);
            }
            Thread.Sleep(intervalMs);
        }
    }

    // Per-page latency histogram bucket upper bounds in microseconds (last bucket is open-ended).
    public static readonly double[] HistUs = [0.3, 1, 2, 4, 8, 32, double.PositiveInfinity];

    private static (long us, long[] hist) Touch(Region r, bool write)
    {
        // The controller touches a small resident "warm" region first so code-path faults are not attributed here.
        // Each page access is bracketed by QueryPerformanceCounter (100 ns resolution, ~20 ns overhead per call).
        long s = 0;
        var hist = new long[HistUs.Length];
        double tickUs = 1e6 / Stopwatch.Frequency;
        long start = Stopwatch.GetTimestamp();
        for (long p = 0; p < r.Pages; p++)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (write) r.Base[p * Sys.Page + 8] = (byte)p;
            else s += r.Base[p * Sys.Page + 8];
            double dt = (Stopwatch.GetTimestamp() - t0) * tickUs;
            int b = 0;
            while (dt >= HistUs[b]) b++;
            hist[b]++;
        }
        long total = Stopwatch.GetTimestamp() - start;
        Interlocked.Add(ref _sink, s);
        return ((long)(total * tickUs), hist);
    }

    /// <summary>Unique, moderately compressible content: 1 KB pseudo-random + 3 KB per-page constant.</summary>
    private static void FillPage(ulong* q, long p)
    {
        ulong s = (ulong)(p + 1) * 0x9E3779B97F4A7C15UL;
        for (int k = 0; k < 128; k++) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; q[k] = s; }
        ulong pat = ((ulong)p * 0x0101010101010101UL) ^ 0xA5A5A5A55A5A5A5AUL;
        for (int k = 128; k < 512; k++) q[k] = pat;
    }

    private static Region Alloc(string name, string kind, long mb, bool fill)
    {
        if (Regions.TryGetValue(name, out var old)) { Free(old); Regions.Remove(name); }
        long bytes = mb * Sys.MB;
        var r = new Region { Name = name, Kind = kind, Pages = bytes / Sys.Page };
        switch (kind)
        {
            case "private":
                r.Base = (byte*)Native.VirtualAlloc(null, (nuint)bytes, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
                if (r.Base == null) throw new InvalidOperationException("VirtualAlloc " + Marshal.GetLastWin32Error());
                if (fill && _reverseFill) for (long p = r.Pages - 1; p >= 0; p--) FillPage((ulong*)(r.Base + p * Sys.Page), p);
                else if (fill) for (long p = 0; p < r.Pages; p++) FillPage((ulong*)(r.Base + p * Sys.Page), p);
                break;
            case "section":
                // Pagefile-backed shared section (CreateFileMapping on INVALID_HANDLE_VALUE).
                r.Map = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, Native.PAGE_READWRITE, (uint)(bytes >> 32), (uint)bytes, null);
                if (r.Map == IntPtr.Zero) throw new InvalidOperationException("CreateFileMapping " + Marshal.GetLastWin32Error());
                r.Base = (byte*)Native.MapViewOfFile(r.Map, Native.FILE_MAP_WRITE | Native.FILE_MAP_READ, 0, 0, (nuint)bytes);
                if (r.Base == null) throw new InvalidOperationException("MapViewOfFile " + Marshal.GetLastWin32Error());
                if (fill) for (long p = 0; p < r.Pages; p++) FillPage((ulong*)(r.Base + p * Sys.Page), p);
                break;
            case "file":
                {
                    // Clean, file-backed, read-only mapped pages (like DLL/resource pages).
                    Directory.CreateDirectory(_tmpDir);
                    r.FilePath = Path.Combine(_tmpDir, $"probe_{Environment.ProcessId}_{name}.bin");
                    var buf = new byte[1 << 20];
                    using (var fs = new FileStream(r.FilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        long pg = 0;
                        fixed (byte* bp = buf)
                        {
                            for (long off = 0; off < bytes; off += buf.Length)
                            {
                                for (int i = 0; i < buf.Length / Sys.Page; i++) FillPage((ulong*)(bp + i * Sys.Page), pg++);
                                fs.Write(buf, 0, buf.Length);
                            }
                        }
                        fs.Flush(true);
                    }
                    r.File = new FileStream(r.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    r.Map = Native.CreateFileMappingW(r.File.SafeFileHandle.DangerousGetHandle(), IntPtr.Zero, Native.PAGE_READONLY, 0, 0, null);
                    if (r.Map == IntPtr.Zero) throw new InvalidOperationException("CreateFileMapping(file) " + Marshal.GetLastWin32Error());
                    r.Base = (byte*)Native.MapViewOfFile(r.Map, Native.FILE_MAP_READ, 0, 0, (nuint)bytes);
                    if (r.Base == null) throw new InvalidOperationException("MapViewOfFile(file) " + Marshal.GetLastWin32Error());
                    if (fill) { long s = 0; for (long p = 0; p < r.Pages; p++) s += r.Base[p * Sys.Page]; Interlocked.Add(ref _sink, s); }
                    break;
                }
            default: throw new ArgumentException("kind " + kind);
        }
        Regions[name] = r;
        return r;
    }

    private static void Free(Region r)
    {
        if (r.Kind == "private") Native.VirtualFree(r.Base, 0, Native.MEM_RELEASE);
        else
        {
            Native.UnmapViewOfFile(r.Base);
            Native.CloseHandle(r.Map);
            r.File?.Dispose();
            if (r.FilePath != null) try { System.IO.File.Delete(r.FilePath); } catch { }
        }
    }

    private static void Cleanup()
    {
        // Stop the hot thread before unmapping, or it faults on freed memory (AccessViolation at exit).
        _stop = true;
        _hot?.Join();
        foreach (var r in Regions.Values) Free(r);
        Regions.Clear();
    }
}
