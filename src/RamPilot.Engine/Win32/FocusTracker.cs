using System.Runtime.InteropServices;
using RamPilot.Core;

namespace RamPilot.Win32;

/// <summary>
/// Foreground tracking. A WinEvent hook (EVENT_SYSTEM_FOREGROUND) wakes the engine immediately; the engine also
/// polls GetForegroundWindow every tick, so a missed event or a failed hook heals within one tick.
/// UWP / packaged windows hosted by ApplicationFrameHost.exe are resolved to the hosted app's process.
/// </summary>
internal static unsafe class FocusTracker
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint hmod,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> proc, uint pid, uint tid, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    public static readonly AutoResetEvent Changed = new(false);

    public static bool HookInstalled { get; private set; }

    public static void Start(Action<string> log)
    {
        using var ready = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            nint a = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnEvent, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            nint b = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, 0, &OnEvent, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            HookInstalled = a != 0;
            if (a == 0) log("warning: foreground hook failed; relying on per-tick polling only");
            if (b == 0) log("warning: minimize-end hook failed");
            ready.Set();
            MSG m;
            while (GetMessageW(&m, 0, 0, 0) > 0) { }
        })
        { IsBackground = true, Name = "focus-hook" };
        t.Start();
        ready.Wait(2000);
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint ev, nint hwnd, int idObj, int idChild, uint thread, uint time) => Changed.Set();

    /// <summary>PID that owns the foreground window, resolving ApplicationFrameHost-hosted windows.</summary>
    public static uint ForegroundPid(ProcessSnapshot snap)
    {
        nint hwnd = Native.GetForegroundWindow();
        if (hwnd == 0) return 0;
        _ = Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0 && snap.TryGet(pid, out var e) && e.Exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var state = new ChildSearch { HostPid = pid };
            _ = Native.EnumChildWindows(hwnd, &OnChild, (nint)(&state));
            if (state.Found != 0) return state.Found;
        }
        return pid;
    }

    private struct ChildSearch { public uint HostPid; public uint Found; }

    [UnmanagedCallersOnly]
    private static int OnChild(nint hwnd, nint lParam)
    {
        var s = (ChildSearch*)lParam;
        _ = Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0 && pid != s->HostPid)
        {
            s->Found = pid;
            return 0;
        }
        return 1;
    }
}

/// <summary>System memory sample including the low-memory resource notification.</summary>
internal sealed class MemoryProbe : IDisposable
{
    private nint _low = Native.CreateMemoryResourceNotification(Native.LowMemoryResourceNotification);

    public bool Read(out MemorySample sample, out uint loadPct)
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m) == 0)
        {
            sample = default;
            loadPct = 0;
            return false;
        }
        bool low = _low != 0 && Native.QueryMemoryResourceNotification(_low, out int state) != 0 && state != 0;
        sample = new MemorySample(m.ullTotalPhys, m.ullAvailPhys, m.ullTotalPageFile, m.ullAvailPageFile, low);
        loadPct = m.dwMemoryLoad;
        return true;
    }

    public void Dispose()
    {
        Native.Close(_low);
        _low = 0;
    }
}
