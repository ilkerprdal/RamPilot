using System.Diagnostics;
using System.Globalization;
using RamPilot.Core;
using RamPilot.Win32;

namespace RamPilot.Safety;

/// <summary>
/// Finds and removes hard working-set limits that no live engine owns: on engine startup (previous run crashed or
/// was killed) and in the guardian after the engine died. Candidates are journaled instances plus every process in
/// an allowlisted tree. Only HARDWS_MAX_ENABLE is cleared; deny-listed images are never opened.
/// </summary>
internal static class LeftoverScanner
{
    public readonly record struct Result(int Cleared, int Failed, Dictionary<string, int> PerApp);

    public static Result ClearAll(EngineConfig cfg, string journalPath, Action<string> log)
    {
        var targets = new HashSet<string>(cfg.Apps.Select(a => a.Exe), StringComparer.OrdinalIgnoreCase);
        var helpers = new HashSet<string>(cfg.Helpers, StringComparer.OrdinalIgnoreCase);
        var snap = new ProcessSnapshot();
        var perApp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int cleared = 0, failed = 0;
        if (!new ProcessSampler().Capture(snap))
        {
            log("leftover scan: process snapshot failed");
            return new Result(0, 1, perApp);
        }

        var candidates = new Dictionary<uint, (ProcEntry E, string App)>();
        foreach (var (pid, create, _) in Journal.Read(journalPath))
            if (snap.TryGet(pid, out var e) && e.CreateTime == create)
                candidates[pid] = (e, snap.FindAppRoot(pid, targets, helpers)?.Exe ?? e.Exe);
        foreach (var e in snap.ByPid.Values)
        {
            if (!targets.Contains(e.Exe) && !helpers.Contains(e.Exe)) continue;
            var root = snap.FindAppRoot(e.Pid, targets, helpers);
            if (root is not null) candidates[e.Pid] = (e, root.Value.Exe);
        }

        foreach (var (e, app) in candidates.Values)
        {
            if (DenyList.IsDeniedExe(e.Exe)) continue;
            using var h = ProcessHandle.Open(e.Pid, e.CreateTime);
            if (h is null) continue;
            if (!h.HardMaxActive()) continue;
            if (h.ClearHardMax(e.WorkingSet))
            {
                cleared++;
                perApp[app] = perApp.GetValueOrDefault(app) + 1;
                log($"leftover scan: cleared hard limit on {e.Exe} ({e.Pid.ToString(CultureInfo.InvariantCulture)}), app {app}");
            }
            else
            {
                failed++;
                log($"leftover scan: FAILED to clear hard limit on {e.Exe} ({e.Pid.ToString(CultureInfo.InvariantCulture)})");
            }
        }
        return new Result(cleared, failed, perApp);
    }
}

/// <summary>
/// `RamPilot.exe --guardian &lt;enginePid&gt;`: a tiny watchdog launched by the engine. It waits on the engine's
/// process handle; when the engine exits for any reason (including TerminateProcess / Task Manager), it clears every
/// hard limit on allowlisted processes and exits. A clean engine exit leaves nothing to clear.
/// </summary>
internal static class Guardian
{
    /// <summary>Single-instance mutex of the engine (one per Windows session).</summary>
    public const string EngineMutexName = @"Local\RamPilot.Engine";

    public static int Run(uint enginePid, EngineConfig cfg, AppPaths paths, Action<string> log)
    {
        nint h = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, 0, enginePid);
        if (h == 0)
        {
            log($"guardian: engine {enginePid} is already gone; scanning now");
        }
        else
        {
            string? image = Native.ImagePath(h);
            if (image is null || !Path.GetFileName(image).Equals(Path.GetFileName(AppInfo.ExePath), StringComparison.OrdinalIgnoreCase))
            {
                Native.Close(h);
                log($"guardian: pid {enginePid} is not a RamPilot engine ({image ?? "unknown"}); exiting");
                return 2;
            }
            log($"guardian: watching engine {enginePid}");
            _ = Native.WaitForSingleObject(h, Native.INFINITE);
            _ = Native.GetExitCodeProcess(h, out uint code);
            Native.Close(h);
            log($"guardian: engine {enginePid} exited with code {code}");
        }

        // A new engine may already be running (fast restart). It can only have started after this guardian's engine
        // died (single-instance mutex), and it clears leftovers at its own startup, before any cap of its own. Its caps
        // are its own, guarded by its own guardian: do not touch them.
        if (Mutex.TryOpenExisting(EngineMutexName, out var other))
        {
            other.Dispose();
            log("guardian: another engine instance is running and has cleared leftovers at its start; exiting");
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var r = LeftoverScanner.ClearAll(cfg, paths.JournalPath, log);
        log($"guardian: cleared {r.Cleared} hard limit(s), {r.Failed} failure(s) in {sw.ElapsedMilliseconds} ms");
        if (r.Failed == 0)
        {
            try { File.Delete(paths.JournalPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return r.Failed == 0 ? 0 : 1;
    }
}

/// <summary>Engine side: keeps one guardian process alive. Caps are only applied while it is alive.</summary>
internal sealed class GuardianLink : IDisposable
{
    private readonly AppPaths _paths;
    private readonly Action<string> _log;
    private Process? _p;
    private long _nextStartMs;
    private int _starts;

    public GuardianLink(AppPaths paths, Action<string> log)
    {
        _paths = paths;
        _log = log;
    }

    public bool Alive
    {
        get
        {
            try { return _p is not null && !_p.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public int? Pid => Alive ? _p!.Id : null;

    public void EnsureRunning(long nowMs)
    {
        if (Alive || nowMs < _nextStartMs) return;
        _nextStartMs = nowMs + 10_000;
        try
        {
            _p?.Dispose();
            var psi = new ProcessStartInfo(AppInfo.ExePath) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--guardian");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (_paths.ConfigOverridden)
            {
                psi.ArgumentList.Add("--config");
                psi.ArgumentList.Add(_paths.ConfigPath);
            }
            _p = Process.Start(psi);
            _starts++;
            _log(_starts == 1 ? $"guardian started (pid {_p?.Id})" : $"guardian restarted (pid {_p?.Id}, start #{_starts})");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _p = null;
            _log("guardian: cannot start: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _p?.Dispose();
        _p = null;
    }
}
