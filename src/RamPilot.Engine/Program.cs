using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using RamPilot;
using RamPilot.Core;
using RamPilot.Ipc;
using RamPilot.Safety;
using RamPilot.Win32;

// RamPilot.exe                     run in the notification area (a second start opens the panel)
// RamPilot.exe --minutes N         run for N minutes, then release everything and exit (tests)
// RamPilot.exe --recover           only clear hard limits left behind by a crashed run
// RamPilot.exe --summary [--detail]  list managed apps, their memory and hard-limit state
// RamPilot.exe --config <path>     use another config.json
// RamPilot.exe --guardian <pid>    internal: watchdog launched by the engine

var opts = Options.Parse(args);
if (opts.Error is not null)
{
    _ = Native.AttachConsole(-1);
    Console.Error.WriteLine("RamPilot: " + opts.Error);
    Console.Error.WriteLine("usage: RamPilot [--minutes N] [--recover] [--summary [--detail]] [--config <path>]");
    return 2;
}
bool console = opts.Summary || opts.Recover || opts.Minutes > 0;
if (console && opts.GuardianPid == 0)
{
    _ = Native.AttachConsole(-1);
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
}

var paths = new AppPaths(opts.ConfigPath);
if (opts.GuardianPid != 0)
{
    var glog = new Logger(paths.GuardianLogPath, console: false);
    var gcfg = ConfigStore.Load(paths, _ => { });
    return Guardian.Run(opts.GuardianPid, gcfg, paths, glog.Info);
}

var logger = new Logger(paths.LogPath, console);
var cfg = ConfigStore.Load(paths, logger.Info);

if (opts.Summary) return Summary.Print(cfg, opts.Detail);

using var mutex = new Mutex(true, Guardian.EngineMutexName, out bool first);
if (!first)
{
    if (opts.Recover)
    {
        Console.WriteLine("The engine is running; it owns all limits. Nothing to recover.");
        return 1;
    }
    PanelLauncher.Open(logger.Info);
    return 0;
}

if (opts.Recover)
{
    var r = LeftoverScanner.ClearAll(cfg, paths.JournalPath, logger.Info);
    if (r.Failed == 0) { try { File.Delete(paths.JournalPath); } catch (IOException) { } }
    Console.WriteLine($"cleared {r.Cleared} hard limit(s), {r.Failed} failure(s)");
    return r.Failed == 0 ? 0 : 1;
}

return EngineHost.Run(cfg, paths, logger, opts.Minutes);

internal sealed record Options(double Minutes, bool Recover, bool Summary, bool Detail, string? ConfigPath, uint GuardianPid, string? Error)
{
    public static Options Parse(string[] args)
    {
        double minutes = 0;
        bool recover = false, summary = false, detail = false;
        string? config = null;
        uint guardian = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--minutes":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) || minutes <= 0 || minutes > 7 * 24 * 60)
                        return Bad("--minutes needs a positive number");
                    break;
                case "--recover": recover = true; break;
                case "--summary": summary = true; break;
                case "--detail": detail = true; break;
                case "--config":
                    config = Next();
                    if (string.IsNullOrWhiteSpace(config)) return Bad("--config needs a path");
                    break;
                case "--guardian":
                    if (!uint.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out guardian) || guardian == 0)
                        return Bad("--guardian needs a process id");
                    break;
                default:
                    return Bad($"unknown argument '{a}'");
            }
        }
        if (detail && !summary) return Bad("--detail requires --summary");
        return new Options(minutes, recover, summary, detail, config, guardian, null);
    }

    private static Options Bad(string e) => new(0, false, false, false, null, 0, e);
}

internal static class EngineHost
{
    public static int Run(EngineConfig cfg, AppPaths paths, Logger logger, double minutes)
    {
        Action<string> log = logger.Info;
        log($"RamPilot {AppInfo.Version} starting (pid {Environment.ProcessId}, tick {cfg.Policy.TickSeconds.ToString(CultureInfo.InvariantCulture)} s"
            + (minutes > 0 ? $", {minutes.ToString(CultureInfo.InvariantCulture)} min" : "") + $", pipe {IpcServer.PipeName})");

        // Refuse to steer by garbage: the process-table layout must match documented APIs.
        string? mismatch = new ProcessSampler().SelfCheck();
        if (mismatch is not null)
        {
            log("FATAL: process table self-check failed, refusing to run: " + mismatch);
            Console.Error.WriteLine("RamPilot: process table self-check failed: " + mismatch);
            return 3;
        }
        log("self-check passed");

        int hr = Native.CoInitializeEx(0, Native.COINIT_MULTITHREADED);
        if (hr < 0) log($"warning: CoInitializeEx 0x{hr:X8}");

        int mainThread = Environment.CurrentManagedThreadId;
        using var stop = new ManualResetEventSlim(false);
        using var cleanupDone = new ManualResetEventSlim(false);
        using var engine = new Engine(cfg, paths, log);
        engine.ClearLeftovers();
        engine.StartGuardian();
        FocusTracker.Start(log);
        using var ipc = new IpcServer(engine.HandleAsync, log);
        ipc.Start();

        Tray.Log = log;
        Tray.OpenPanel = () => PanelLauncher.Open(log);
        Tray.TogglePause = engine.TogglePause;
        Tray.Quit = engine.RequestQuit;
        Tray.IsPaused = () => engine.Paused;
        Tray.Tooltip = () => engine.Tooltip;
        Tray.Start();

        // Every shutdown path ends on the main thread, which owns the process list. Other threads only signal
        // and wait (bounded); the guardian covers anything that does not get that far.
        void RequestStop()
        {
            stop.Set();
            try { engine.Wake.Set(); } catch (ObjectDisposedException) { }
        }
        using var r1 = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; RequestStop(); });
        using var r2 = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; RequestStop(); cleanupDone.Wait(5000); });
        using var r3 = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, c => { c.Cancel = true; RequestStop(); cleanupDone.Wait(5000); });
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            RequestStop();
            if (Environment.CurrentManagedThreadId != mainThread) cleanupDone.Wait(5000);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log("UNHANDLED EXCEPTION: " + e.ExceptionObject);
            if (Environment.CurrentManagedThreadId == mainThread) return;   // the main thread's finally runs cleanup
            RequestStop();
            cleanupDone.Wait(5000);
        };

        long until = minutes > 0 ? Environment.TickCount64 + (long)(minutes * 60_000) : long.MaxValue;
        var waits = new WaitHandle[] { FocusTracker.Changed, engine.Wake, stop.WaitHandle };
        long tick = engine.TickMs;
        long next = Environment.TickCount64;
        try
        {
            while (!stop.IsSet && !engine.QuitRequested)
            {
                long now = Environment.TickCount64;
                if (now >= until) break;
                if (now >= next)
                {
                    engine.Tick(now);
                    next += tick;
                    if (next <= now) next = now + tick;   // missed ticks (sleep, debugger) are skipped, not replayed
                }
                engine.ProcessInbox();
                int wait = (int)Math.Clamp(next - Environment.TickCount64, 0, tick);
                int signaled = WaitHandle.WaitAny(waits, wait);
                if (signaled == 0) engine.OnFocusChanged();   // focus events release immediately; they never sample
            }
        }
        finally
        {
            Tray.Stop();
            engine.ProcessInbox();
            bool ok = engine.ReleaseAll();
            log(ok ? "exit: all limits released" : "exit: some limits could not be released; the guardian will retry");
            cleanupDone.Set();
        }
        return 0;
    }
}

internal static class PanelLauncher
{
    public static void Open(Action<string> log)
    {
        var panel = Path.Combine(AppContext.BaseDirectory, "RamPilot.Panel.exe");
        if (!File.Exists(panel)) { log("panel not found: " + panel); return; }
        try
        {
            foreach (var p in Process.GetProcessesByName("RamPilot.Panel"))
            {
                using (p)
                {
                    if (p.MainWindowHandle == 0) continue;
                    _ = Native.ShowWindow(p.MainWindowHandle, 9 /*SW_RESTORE*/);
                    _ = Native.SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
            using var started = Process.Start(new ProcessStartInfo(panel) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log("cannot start panel: " + ex.Message);
        }
    }
}

internal static class Summary
{
    public static int Print(EngineConfig cfg, bool detail)
    {
        var targets = new HashSet<string>(cfg.Apps.Select(a => a.Exe), StringComparer.OrdinalIgnoreCase);
        var helpers = new HashSet<string>(cfg.Helpers, StringComparer.OrdinalIgnoreCase);
        var snap = new ProcessSnapshot();
        if (!new ProcessSampler().Capture(snap)) { Console.Error.WriteLine("snapshot failed"); return 1; }

        _ = Native.CoInitializeEx(0, Native.COINIT_MULTITHREADED);
        var audioPids = new HashSet<uint>();
        bool audioOk = new AudioSessions(_ => { }).Poll(audioPids);
        var probe = new MemoryProbe();
        probe.Read(out var mem, out _);
        probe.Dispose();
        Console.WriteLine($"memory: avail {AppInfo.Mb(mem.AvailPhys)} of {AppInfo.Mb(mem.TotalPhys)} MB ({AppInfo.F1(mem.AvailPct)}%), commit {AppInfo.F1(mem.CommitPct)}%, pressure {Governor.Name(Governor.Classify(mem, cfg.Governor))}, low-memory signal {mem.LowMemorySignaled}");
        Console.WriteLine($"audio: {(audioOk ? (audioPids.Count == 0 ? "no active sessions" : "active pids " + string.Join(' ', audioPids)) : "unavailable")}");

        var groups = snap.ByPid.Values
            .Where(e => targets.Contains(e.Exe) || helpers.Contains(e.Exe))
            .Select(e => (e, root: snap.FindAppRoot(e.Pid, targets, helpers)))
            .Where(x => x.root is not null)
            .GroupBy(x => (x.root!.Value.Exe, x.root.Value.Pid));
        int capped = 0;
        foreach (var g in groups)
        {
            ulong ws = 0, pws = 0;
            foreach (var x in g) { ws += x.e.WorkingSet; pws += x.e.PrivateWorkingSet; }
            bool audio = g.Any(x => audioPids.Contains(x.e.Pid));
            Console.WriteLine($"{g.Key.Exe}#{g.Key.Pid}  {g.Count()} processes  ws={AppInfo.Mb(ws)} MB  private_ws={AppInfo.Mb(pws)} MB{(audio ? "  [audio]" : "")}");
            foreach (var x in g.OrderBy(x => x.e.CreateTime))
            {
                using var h = ProcessHandle.Open(x.e.Pid, x.e.CreateTime);
                bool hard = h?.HardMaxActive() ?? false;
                if (hard) capped++;
                if (!detail) continue;
                var cls = RoleClassifier.Classify(h?.ReadCommandLine(), cfg.ExcludeCommandLine);
                string max = "";
                if (h is not null && hard) max = $" hardmax={AppInfo.Mb(h.CurrentMax())} MB";
                Console.WriteLine($"    {x.e.Pid,6} {x.e.Exe,-20} {cls.Role,-28} ws={AppInfo.Mb(x.e.WorkingSet),7} pws={AppInfo.Mb(x.e.PrivateWorkingSet),7} commit={AppInfo.Mb(x.e.PrivateBytes),7} MB  hard_limit={(hard ? "ON" : "off")}{max}{(cls.Excluded ? "  [excluded: " + cls.Reason + "]" : "")}");
            }
        }
        Console.WriteLine($"processes with a hard working-set limit: {capped}");
        return 0;
    }
}
