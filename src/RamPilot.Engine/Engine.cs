using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RamPilot.Core;
using RamPilot.Ipc;
using RamPilot.Safety;
using RamPilot.Win32;

namespace RamPilot;

/// <summary>
/// The Win32 shell around <see cref="Controller"/>: samples on a fixed cadence, applies the controller's actions,
/// keeps the journal and guardian in step, and publishes status. Every piece of state is owned by the main loop
/// thread; IPC and tray requests are posted to an inbox and run there.
/// </summary>
internal sealed class Engine : IDisposable
{
    private const long HistoryEveryMs = 10_000, CsvEveryMs = 10_000, ProfilesEveryMs = 60_000;
    private const long ResumeGraceMs = 60_000, StartupGraceMs = 0;
    /// <summary>If the guardian cannot be kept alive this long (3 restart attempts), every cap is released.</summary>
    private const long GuardianDownReleaseMs = 30_000;
    private const int MaxEvents = 50, MaxHistory = 180, StoreObservationTicks = 15;

    private readonly EngineConfig _cfg;
    private readonly PolicyConfig _p;
    private readonly AppPaths _paths;
    private readonly Action<string> _log;
    private readonly HashSet<string> _targets, _helpers;
    private readonly HashSet<string> _disabled;
    private readonly ProcessSampler _sampler = new();
    private readonly ProcessSnapshot _snap = new();
    private readonly MemoryProbe _memory = new();
    private readonly AudioSessions _audio;
    private readonly Governor _governor = new();
    private readonly Journal _journal;
    private readonly GuardianLink _guardian;
    private readonly LearnedProfiles _profiles;
    private readonly StoreRatioEstimator _storeRatio = new();
    private readonly Dictionary<uint, TrackedProcess> _procs = new();
    private readonly HashSet<(uint, long)> _unopenable = [];
    private readonly Dictionary<string, long> _appFocusMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _audioPids = [];
    private readonly HashSet<string> _audioApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<EventDto> _events = new();
    private readonly Queue<HistoryDto> _history = new();
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly Dictionary<(string App, ReleaseReason Reason), (int Count, string Role)> _releases = [];
    private readonly List<uint> _gone = [];
    private readonly Dictionary<uint, ulong> _obsPrivate = [];
    private readonly long _startMs = Environment.TickCount64;
    private readonly StreamWriter? _csv;

    private long _lastTickMs, _graceUntilMs, _lastHistoryMs = -HistoryEveryMs, _lastCsvMs, _lastProfilesMs;
    private int _gen, _snapshotFailures, _tickErrors;
    private bool _profilesDirty, _canCap;
    private MemorySample _mem;
    private uint _loadPct;
    private ulong _compression;
    private uint _compressionPid;
    private string? _fgApp;
    private long _guardianDownSinceMs;
    private bool _guardianReleased;
    private bool _obsActive;
    private int _obsTicks;
    private ulong _obsStore0;
    private volatile string _tooltipText = "RamPilot";
    private volatile bool _paused;

    public Engine(EngineConfig cfg, AppPaths paths, Action<string> log)
    {
        _cfg = cfg;
        _p = cfg.Policy;
        _paths = paths;
        _log = log;
        _targets = new HashSet<string>(cfg.Apps.Select(a => a.Exe), StringComparer.OrdinalIgnoreCase);
        _helpers = new HashSet<string>(cfg.Helpers, StringComparer.OrdinalIgnoreCase);
        _disabled = Settings.LoadDisabled(paths.DisabledPath);
        _audio = new AudioSessions(log);
        _journal = new Journal(paths.JournalPath);
        _guardian = new GuardianLink(paths, log);
        _profiles = LoadProfiles();
        _csv = OpenCsv();
        Event("info", "engine_started");
    }

    public AutoResetEvent Wake { get; } = new(false);
    public bool Paused => _paused;
    public bool QuitRequested { get; private set; }
    public string Tooltip => _tooltipText;
    public long TickMs => _p.TickMs;

    // ------------------------------------------------------------------ startup

    /// <summary>Clears hard limits left by a previous run that died. Must run before the first tick.</summary>
    public void ClearLeftovers()
    {
        var r = LeftoverScanner.ClearAll(_cfg, _paths.JournalPath, _log);
        foreach (var kv in r.PerApp)
            Event("warn", "leftover_cleared", app: kv.Key, count: kv.Value);
        if (r.Failed == 0) _journal.Delete();
        else Event("warn", "error", detail: $"{r.Failed} leftover hard limit(s) could not be cleared");
    }

    public void StartGuardian() => _guardian.EnsureRunning(Environment.TickCount64);

    // ------------------------------------------------------------------ inbox / IPC (any thread)

    public void Post(Action a)
    {
        _inbox.Enqueue(a);
        Wake.Set();
    }

    public void ProcessInbox()
    {
        while (_inbox.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log("inbox action failed: " + ex);
            }
        }
    }

    public async Task<string> HandleAsync(RequestDto req)
    {
        switch (req.Cmd)
        {
            case "hello":
                if (req.Protocol != ProtocolInfo.Version)
                    return JsonSerializer.Serialize(new ReplyDto
                    {
                        Ok = false,
                        Error = "protocol_mismatch",
                        Protocol = ProtocolInfo.Version,
                        EngineVersion = AppInfo.Version,
                    }, IpcJson.Default.ReplyDto);
                return JsonSerializer.Serialize(new ReplyDto { Ok = true, Protocol = ProtocolInfo.Version, EngineVersion = AppInfo.Version },
                    IpcJson.Default.ReplyDto);
            case "status":
                {
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Post(() => tcs.TrySetResult(BuildStatusJson()));
                    return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            case "pause":
                Post(() => SetPaused(true));
                return IpcServer.Ok();
            case "resume":
                Post(() => SetPaused(false));
                return IpcServer.Ok();
            case "setApp":
                if (string.IsNullOrEmpty(req.Exe) || req.Enabled is null) return IpcServer.Error("bad_request");
                if (!_targets.Contains(req.Exe)) return IpcServer.Error("not_allowed");
                string exe = req.Exe;
                bool en = req.Enabled.Value;
                Post(() => SetApp(exe, en));
                return IpcServer.Ok();
            case "setAutostart":
                if (req.Enabled is null) return IpcServer.Error("bad_request");
                bool a = req.Enabled.Value;
                Post(() =>
                {
                    if (Settings.SetAutostart(a)) Event("info", a ? "autostart_on" : "autostart_off");
                    else Event("warn", "error", detail: "cannot write the Run registry key");
                });
                return IpcServer.Ok();
            case "quit":
                Post(() => QuitRequested = true);
                return IpcServer.Ok();
            default:
                return IpcServer.Error("unknown_cmd");
        }
    }

    public void TogglePause() => Post(() => SetPaused(!_paused));

    public void RequestQuit() => Post(() => QuitRequested = true);

    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused)
        {
            long now = Environment.TickCount64;
            foreach (var tp in _procs.Values)
                if (!tp.Excluded) Apply(tp, Controller.Decide(tp.State, default, _p, new DecisionContext { NowMs = now, Paused = true, CapIntact = true }), now);
        }
        Event("info", paused ? "paused" : "resumed");
        UpdateTooltip();
    }

    private void SetApp(string exe, bool enabled)
    {
        bool changed = enabled ? _disabled.Remove(exe) : _disabled.Add(exe);
        if (!changed) return;
        if (!Settings.SaveDisabled(_paths.DisabledPath, _disabled)) Event("warn", "error", detail: "cannot save disabled.txt");
        if (!enabled)
        {
            long now = Environment.TickCount64;
            foreach (var tp in _procs.Values)
                if (!tp.Excluded && tp.AppExe.Equals(exe, StringComparison.OrdinalIgnoreCase))
                    Apply(tp, Controller.Decide(tp.State, default, _p, new DecisionContext { NowMs = now, Disabled = true, CapIntact = true }), now);
        }
        Event("info", enabled ? "app_enabled" : "app_disabled", app: exe);
    }

    // ------------------------------------------------------------------ focus (between ticks)

    /// <summary>Called when the foreground changed: releases the new foreground app at once, without waiting for a tick.</summary>
    public void OnFocusChanged()
    {
        if (!_snap.Valid) return;
        long now = Environment.TickCount64;
        uint fg = FocusTracker.ForegroundPid(_snap);
        string? app = fg == 0 ? null : _snap.FindAppRoot(fg, _targets, _helpers)?.Exe;
        _fgApp = app;
        if (app is null) return;
        _appFocusMs[app] = now;
        foreach (var tp in _procs.Values)
        {
            if (tp.Excluded || !tp.State.IsCapped || !tp.AppExe.Equals(app, StringComparison.OrdinalIgnoreCase)) continue;
            Apply(tp, Controller.Decide(tp.State, default, _p, new DecisionContext { NowMs = now, Focused = true, CapIntact = true }), now);
        }
        FlushReleaseEvents();
    }

    // ------------------------------------------------------------------ tick (fixed cadence)

    /// <summary>One measurement + decision round. Never throws: an I/O or interop error costs one tick, not the engine.</summary>
    public void Tick(long now)
    {
        try
        {
            TickCore(now);
            _tickErrors = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _tickErrors++;
            _log("tick failed: " + ex);
            if (_tickErrors == 1 || _tickErrors % 30 == 0) Event("warn", "error", detail: "tick failed: " + ex.GetType().Name);
        }
    }

    private void TickCore(long now)
    {
        if (!_sampler.Capture(_snap))
        {
            // No information is not "every process exited": keep all state, decide nothing.
            if (_snapshotFailures++ % 30 == 0) _log("snapshot failed: " + _sampler.LastError);
            return;
        }
        _snapshotFailures = 0;

        bool resumed = _lastTickMs != 0 && now - _lastTickMs > 3 * _p.TickMs;
        if (resumed)
        {
            _graceUntilMs = now + ResumeGraceMs;
            _log($"resume detected (gap {(now - _lastTickMs) / 1000} s): holding all caps for {ResumeGraceMs / 1000} s");
        }
        double dt = _lastTickMs == 0 ? _p.TickSeconds : (now - _lastTickMs) / 1000.0;
        bool deltasValid = _lastTickMs != 0 && !resumed;
        _lastTickMs = now;
        bool grace = now < _graceUntilMs || now - _startMs < StartupGraceMs;

        if (_memory.Read(out var mem, out uint load)) { _mem = mem; _loadPct = load; }
        _governor.Update(_mem, _cfg.Governor, now);
        ReadCompressionStore();

        uint fg = FocusTracker.ForegroundPid(_snap);
        _fgApp = fg == 0 ? null : _snap.FindAppRoot(fg, _targets, _helpers)?.Exe;
        if (_fgApp is not null) _appFocusMs[_fgApp] = now;

        if (_audio.Poll(_audioPids))
        {
            _audioApps.Clear();
            foreach (uint pid in _audioPids)
                if (_snap.FindAppRoot(pid, _targets, _helpers) is { } root) _audioApps.Add(root.Exe);
        }   // on failure the previous set stays in force (sticky)

        _guardian.EnsureRunning(now);
        _canCap = _guardian.Alive;

        Reconcile(dt, deltasValid, now);
        CheckGuardian(now);
        RetryPendingReleases();

        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var tp in _procs.Values)
        {
            if (tp.Excluded) continue;
            var ctx = new DecisionContext
            {
                NowMs = now,
                NowUnix = unix,
                Paused = _paused,
                Disabled = _disabled.Contains(tp.AppExe),
                Focused = _fgApp is not null && tp.AppExe.Equals(_fgApp, StringComparison.OrdinalIgnoreCase),
                Audio = _audioApps.Contains(tp.AppExe),
                LastAppFocusMs = _appFocusMs.TryGetValue(tp.AppExe, out long f) ? f : _startMs,
                Aggressiveness = _governor.Aggressiveness,
                Grace = grace,
                CapIntact = !tp.State.IsCapped || tp.Handle.HardMaxActive(),
                ProcessAgeMs = (nowFileTime - tp.CreateTime) / 10_000,
            };
            Apply(tp, Controller.Decide(tp.State, tp.Sample(), _p, ctx), now);
            if (tp.State.LearnedDirty)
            {
                tp.State.LearnedDirty = false;
                if (_profiles.Update(tp.ProfileKey, tp.State, unix)) _profilesDirty = true;
            }
        }
        FlushReleaseEvents();
        ObserveStore();

        if (now - _lastCsvMs >= CsvEveryMs) { _lastCsvMs = now; WriteCsv(); }
        if (now - _lastHistoryMs >= HistoryEveryMs) { _lastHistoryMs = now; AddHistory(); }
        if (_profilesDirty && now - _lastProfilesMs >= ProfilesEveryMs) { _lastProfilesMs = now; SaveProfiles(); }
        _journal.FlushIfDirty();
        UpdateTooltip();
    }

    /// <summary>
    /// Caps are only tightened while the guardian runs. If it stays down (exe removed, blocked by antivirus), existing
    /// caps would be unguarded, so after <see cref="GuardianDownReleaseMs"/> every cap is released. Capping resumes when
    /// a guardian is running again (first-cap rules still apply).
    /// </summary>
    private void CheckGuardian(long now)
    {
        if (_canCap)
        {
            _guardianDownSinceMs = 0;
            _guardianReleased = false;
            return;
        }
        if (_guardianDownSinceMs == 0) _guardianDownSinceMs = now;
        if (_guardianReleased || now - _guardianDownSinceMs < GuardianDownReleaseMs) return;
        _guardianReleased = true;
        int n = 0;
        foreach (var tp in _procs.Values)
        {
            if (tp.Excluded || !tp.State.IsCapped) continue;
            var a = Controller.Shutdown(tp.State, now);
            Apply(tp, a with { Release = ReleaseReason.Error }, now);
            n++;
        }
        _log($"guardian has not been running for {GuardianDownReleaseMs / 1000} s: released {n} cap(s); capping is suspended until it runs again");
        Event("warn", "error", detail: "guardian not running; all limits released");
        FlushReleaseEvents();
    }

    /// <summary>Retries releases that failed; their journal entries stay until the limit is really gone.</summary>
    private void RetryPendingReleases()
    {
        foreach (var tp in _procs.Values)
        {
            if (!tp.ReleasePending) continue;
            if (tp.State.IsCapped) { tp.ReleasePending = false; continue; }   // capped again on purpose
            if (tp.Handle.ClearHardMax(tp.Last.WorkingSet) || !tp.Handle.HardMaxActive())
            {
                tp.ReleasePending = false;
                _journal.Forget(tp.Pid, tp.CreateTime);
                _log($"release retry succeeded for {tp.Describe()}");
            }
        }
    }

    private void ReadCompressionStore()
    {
        if (_compressionPid != 0 && _snap.TryGet(_compressionPid, out var mc) && mc.Exe == "Memory Compression")
        {
            _compression = mc.WorkingSet;
            return;
        }
        foreach (var e in _snap.Entries)
            if (e.Exe == "Memory Compression")
            {
                _compressionPid = e.Pid;
                _compression = e.WorkingSet;
                return;
            }
    }

    /// <summary>Opens new members of allowlisted trees, updates known ones, drops exited ones.</summary>
    private void Reconcile(double dt, bool deltasValid, long now)
    {
        _gen++;
        foreach (var e in _snap.Entries)
        {
            if (!_targets.Contains(e.Exe) && !_helpers.Contains(e.Exe)) continue;
            var root = _snap.FindAppRoot(e.Pid, _targets, _helpers);
            if (root is null) continue;
            if (_procs.TryGetValue(e.Pid, out var tp))
            {
                if (tp.CreateTime == e.CreateTime)
                {
                    tp.Update(e, dt, deltasValid);
                    tp.AppExe = root.Value.Exe;
                    tp.RootPid = root.Value.Pid;
                    tp.SeenGen = _gen;
                    continue;
                }
                Drop(tp, "pid reused");
            }
            if (_unopenable.Contains((e.Pid, e.CreateTime))) continue;
            tp = Open(e, root.Value);
            if (tp is null) { _unopenable.Add((e.Pid, e.CreateTime)); continue; }
            tp.SeenGen = _gen;
            _procs[e.Pid] = tp;
            if (!_appFocusMs.ContainsKey(tp.AppExe)) _appFocusMs[tp.AppExe] = now;
        }

        _gone.Clear();
        foreach (var tp in _procs.Values)
            if (tp.SeenGen != _gen) _gone.Add(tp.Pid);
        foreach (uint pid in _gone) Drop(_procs[pid], "exited");
        if (_unopenable.Count > 4096) _unopenable.Clear();
    }

    private TrackedProcess? Open(in ProcEntry e, in ProcEntry root)
    {
        if (DenyList.IsDeniedExe(e.Exe)) return null;
        var h = ProcessHandle.Open(e.Pid, e.CreateTime);
        if (h is null)
        {
            _log($"skip: cannot open {e.Exe} ({e.Pid})");
            return null;
        }
        string? image = h.ImagePath();
        if (DenyList.IsDeniedPath(image))
        {
            _log($"skip: {e.Exe} ({e.Pid}) runs from a deny-listed location");
            h.Dispose();
            return null;
        }
        var cls = RoleClassifier.Classify(h.ReadCommandLine(), _cfg.ExcludeCommandLine);
        var tp = new TrackedProcess(h, e, root.Exe, root.Pid, cls);
        // Unknown history counts as busy: the first-cap idle period starts when we first see the process.
        tp.State.LastBusyMs = Environment.TickCount64;
        if (!tp.Excluded) _profiles.Seed(tp.ProfileKey, tp.State);
        _log($"tracking {tp.Describe()} ws={AppInfo.Mb(e.WorkingSet)} MB" + (tp.Excluded ? $" [excluded: {tp.ExcludeReason}]" : "")
             + (tp.State.LearnedCap > 0 ? $" learned={AppInfo.Mb(tp.State.LearnedCap)} MB" : ""));
        return tp;
    }

    private void Drop(TrackedProcess tp, string why)
    {
        _procs.Remove(tp.Pid);
        _obsPrivate.Remove(tp.Pid);
        bool cleared = true;
        if ((tp.State.IsCapped || tp.ReleasePending) && tp.Handle.IsAlive) cleared = tp.Handle.ClearHardMax(tp.Last.WorkingSet);
        if (cleared) _journal.Forget(tp.Pid, tp.CreateTime);   // else: the guardian / next start clears it
        if (!tp.Excluded) _log($"untracked {tp.Describe()} ({why})");
        tp.Dispose();
    }

    // ------------------------------------------------------------------ applying actions

    private void Apply(TrackedProcess tp, ControllerAction a, long now)
    {
        if (a.Kind == ActionKind.None) return;
        if (a.Kind == ActionKind.Release)
        {
            bool ok = tp.Handle.ClearHardMax(tp.Last.WorkingSet);
            tp.Savings.End();
            if (ok) _journal.Forget(tp.Pid, tp.CreateTime);
            else tp.ReleasePending = true;   // keep the journal entry; retried every tick
            _log($"release {tp.Describe()} reason={ReasonName(a.Release)} prev={AppInfo.Mb(a.PrevCapBytes)} MB" + (ok ? "" : " FAILED"));
            if (a.Release is ReleaseReason.Focus or ReleaseReason.Active or ReleaseReason.Audio or ReleaseReason.Error)
            {
                var key = (tp.AppExe, a.Release);
                var cur = _releases.GetValueOrDefault(key);
                _releases[key] = (cur.Count + 1, cur.Count == 0 ? tp.Role : "multiple");
            }
            return;
        }

        bool tightening = a.Trims || a.Kind == ActionKind.Reapply;
        if (tightening && !_canCap)
        {
            Fail(tp, a, now, "guardian not running");
            return;
        }
        if (!_journal.Ensure(tp.Pid, tp.CreateTime, tp.Exe))
        {
            Fail(tp, a, now, "journal cannot be written");
            return;
        }
        if (a.Trims) NoteTrim(tp);
        tp.ReleasePending = false;
        if (!tp.Handle.SetHardMax(a.CapBytes))
        {
            Fail(tp, a, now, "SetProcessWorkingSetSizeEx failed");
            return;
        }
        if (a.Kind is ActionKind.StartDescent or ActionKind.RestoreLearned)
            tp.Savings.Begin(tp.Last.PrivateWorkingSet, tp.Last.PrivateBytes);

        string ws = $"ws={AppInfo.Mb(tp.Last.WorkingSet)} pws={AppInfo.Mb(tp.Last.PrivateWorkingSet)} MB";
        switch (a.Kind)
        {
            case ActionKind.StartDescent:
                _log($"descent {tp.Describe()} cap={AppInfo.Mb(a.CapBytes)} MB {ws} pressure={Governor.Name(_governor.Level)}");
                Event("cap", "descent_started", tp.AppExe, tp.Role, a.CapBytes);
                break;
            case ActionKind.Lower:
                _log($"lower {tp.Describe()} {AppInfo.Mb(a.PrevCapBytes)} -> {AppInfo.Mb(a.CapBytes)} MB {ws}");
                Event("cap", "cap_lowered", tp.AppExe, tp.Role, a.CapBytes, a.PrevCapBytes);
                break;
            case ActionKind.Raise:
                string reason = a.Raise == RaiseReason.Stall ? "stall" : "refaults";
                _log($"raise {tp.Describe()} {AppInfo.Mb(a.PrevCapBytes)} -> {AppInfo.Mb(a.CapBytes)} MB reason={reason} faults={tp.FaultDelta} hard={tp.HardDelta} floor={AppInfo.Mb(tp.State.FloorBytes)} MB");
                Event("warn", "cap_raised", tp.AppExe, tp.Role, a.CapBytes, a.PrevCapBytes, reason);
                break;
            case ActionKind.RestoreLearned:
                _log($"restore {tp.Describe()} cap={AppInfo.Mb(a.CapBytes)} MB learned={AppInfo.Mb(tp.State.LearnedCap)} MB {ws}");
                Event("cap", "cap_restored_learned", tp.AppExe, tp.Role, a.CapBytes);
                break;
            case ActionKind.Reapply:
                _log($"tampered {tp.Describe()}: hard limit was removed by the target; re-applied {AppInfo.Mb(a.CapBytes)} MB");
                Event("warn", "cap_tampered", tp.AppExe, tp.Role);
                break;
        }
    }

    private void Fail(TrackedProcess tp, ControllerAction a, long now, string why)
    {
        bool wasCapped = tp.State.IsCapped && a.Kind is not (ActionKind.StartDescent or ActionKind.RestoreLearned);
        Controller.OnApplyFailed(tp.State, now);
        tp.Savings.End();
        if ((wasCapped || a.PrevCapBytes != 0) && !tp.Handle.ClearHardMax(tp.Last.WorkingSet)) tp.ReleasePending = true;
        _log($"cannot apply {a.Kind} to {tp.Describe()}: {why}; released");
        var key = (tp.AppExe, ReleaseReason.Error);
        var cur = _releases.GetValueOrDefault(key);
        _releases[key] = (cur.Count + 1, cur.Count == 0 ? tp.Role : "multiple");
    }

    private void FlushReleaseEvents()
    {
        foreach (var kv in _releases)
            Event("free", "cap_released", kv.Key.App, kv.Value.Role, reason: ReasonName(kv.Key.Reason),
                count: kv.Value.Count > 1 ? kv.Value.Count : null);
        _releases.Clear();
    }

    private static string ReasonName(ReleaseReason r) => r switch
    {
        ReleaseReason.Focus => "focus",
        ReleaseReason.Active => "active",
        ReleaseReason.Audio => "audio",
        ReleaseReason.Paused => "paused",
        ReleaseReason.Disabled => "disabled",
        ReleaseReason.PressureLow => "pressure_low",
        ReleaseReason.Shutdown => "shutdown",
        _ => "error",
    };

    // ------------------------------------------------------------------ store-cost estimation

    private void NoteTrim(TrackedProcess tp)
    {
        if (!_obsActive)
        {
            _obsActive = true;
            _obsTicks = 0;
            _obsStore0 = _compression;
            _obsPrivate.Clear();
        }
        if (_obsTicks < 2) _obsPrivate.TryAdd(tp.Pid, tp.Last.PrivateWorkingSet);   // only trims at the start of the window
    }

    /// <summary>
    /// ~30 s after a trim: private bytes trimmed vs. compression store growth over the same window. The window is
    /// long because the store absorbs modified pages lazily.
    /// </summary>
    private void ObserveStore()
    {
        if (!_obsActive || ++_obsTicks < StoreObservationTicks) return;
        ulong trimmed = 0;
        foreach (var kv in _obsPrivate)
            if (_procs.TryGetValue(kv.Key, out var tp) && kv.Value > tp.Last.PrivateWorkingSet)
                trimmed += kv.Value - tp.Last.PrivateWorkingSet;
        long delta = (long)_compression - (long)_obsStore0;
        if (_storeRatio.Observe(trimmed, delta))
            _log($"store: trimmed {AppInfo.Mb(trimmed)} MB private, store {(delta >= 0 ? "+" : "")}{(delta / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)} MB -> ratio {_storeRatio.Ratio.ToString("F2", CultureInfo.InvariantCulture)}");
        _obsActive = false;
        _obsPrivate.Clear();
    }

    private SavingsSummary Savings()
    {
        ulong reclaimed = 0;
        foreach (var tp in _procs.Values) reclaimed += tp.Reclaimed;
        return Core.Savings.Summarize(reclaimed, _storeRatio.Ratio);
    }

    // ------------------------------------------------------------------ status

    private string BuildStatusJson()
    {
        long now = Environment.TickCount64;
        _snap.TryGet(_sampler.SelfPid, out var self);
        var sv = Savings();
        var s = new StatusDto
        {
            EngineVersion = AppInfo.Version,
            Paused = _paused,
            Autostart = Settings.Autostart,
            UptimeSec = (now - _startMs) / 1000,
            Engine = new EngineDto { WsMb = R(self.WorkingSet), PrivateMb = R(self.PrivateWorkingSet) },
            System = new SystemDto
            {
                TotalMb = R(_mem.TotalPhys),
                AvailMb = R(_mem.AvailPhys),
                CompressionMb = R(_compression),
                LoadPct = (int)_loadPct,
                CommitPct = (int)Math.Round(_mem.CommitPct),
            },
            Governor = new GovernorDto { Pressure = Governor.Name(_governor.Level), Aggressiveness = Math.Round(_governor.Aggressiveness, 2) },
            Savings = new SavingsDto { ReclaimedMb = sv.ReclaimedMb, StoreCostMb = sv.StoreCostMb, NetMb = sv.NetMb },
        };

        var shownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = _cfg.Apps.OrderByDescending(a => _procs.Values.Any(p => p.AppExe.Equals(a.Exe, StringComparison.OrdinalIgnoreCase)));
        foreach (var app in ordered)
        {
            string name = _cfg.DisplayName(app.Exe);
            var list = _procs.Values.Where(p => !p.Excluded && p.AppExe.Equals(app.Exe, StringComparison.OrdinalIgnoreCase)).ToList();
            bool running = list.Count > 0;
            if (!shownNames.Add(name) && !running) continue;   // e.g. WhatsApp.exe vs WhatsApp.Root.exe
            bool enabled = !_disabled.Contains(app.Exe);
            var d = new AppDto { Exe = app.Exe, Name = name, Enabled = enabled, Running = running };
            if (running)
            {
                d.ProcCount = _procs.Values.Count(p => p.AppExe.Equals(app.Exe, StringComparison.OrdinalIgnoreCase));
                d.Focused = app.Exe.Equals(_fgApp, StringComparison.OrdinalIgnoreCase);
                d.Audio = _audioApps.Contains(app.Exe);
                d.WsMb = R(Sum(list, p => p.Last.WorkingSet));
                d.PrivateWsMb = R(Sum(list, p => p.Last.PrivateWorkingSet));
                d.CapMb = R(Sum(list, p => p.State.CapBytes));
                d.ReclaimedMb = R(Sum(list, p => p.Reclaimed));
                d.State = !enabled || _paused ? "Disabled" : AppState(list);
            }
            else d.State = enabled ? "NotRunning" : "Disabled";
            s.Apps.Add(d);
        }
        s.Events.AddRange(_events);
        s.History.AddRange(_history);
        return JsonSerializer.Serialize(s, IpcJson.Default.StatusDto);
    }

    private string AppState(List<TrackedProcess> list)
    {
        CapState? only = null;
        bool mixed = false;
        foreach (var p in list)
        {
            // Processes too small to manage do not make an app "Mixed".
            if (!p.State.IsCapped && p.Last.WorkingSet < _p.MinWsToManage) continue;
            if (only is null) only = p.State.State;
            else if (only != p.State.State) mixed = true;
        }
        if (mixed) return "Mixed";
        return only switch
        {
            CapState.Descending => "Descending",
            CapState.Holding => "Holding",
            _ => "Free",
        };
    }

    private static ulong Sum(List<TrackedProcess> list, Func<TrackedProcess, ulong> f)
    {
        ulong s = 0;
        foreach (var p in list) s += f(p);
        return s;
    }

    private static double R(ulong bytes) => Units.Round1(Units.ToMb(bytes));

    private void AddHistory()
    {
        ulong managed = 0;
        foreach (var tp in _procs.Values) if (!tp.Excluded) managed += tp.Last.PrivateWorkingSet;
        _history.Enqueue(new HistoryDto
        {
            T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            AvailMb = R(_mem.AvailPhys),
            ManagedMb = R(managed),
            NetMb = Savings().NetMb,
        });
        while (_history.Count > MaxHistory) _history.Dequeue();
    }

    private void UpdateTooltip()
    {
        double net = Math.Round(Savings().NetMb);
        if (_paused == _tooltipPaused && net == _tooltipNet) return;   // no new string per tick
        _tooltipPaused = _paused;
        _tooltipNet = net;
        _tooltipText = _paused ? Strings.TooltipPaused : Strings.TooltipNet(net);
    }

    private bool _tooltipPaused;
    private double _tooltipNet = -1;

    private void Event(string kind, string code, string? app = null, string? role = null, ulong? capBytes = null,
        ulong? prevCapBytes = null, string? reason = null, int? count = null, string? detail = null)
    {
        _events.Enqueue(new EventDto
        {
            T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = kind,
            Code = code,
            App = app,
            Role = role,
            CapMb = capBytes is { } c ? R(c) : null,
            PrevCapMb = prevCapBytes is { } pc ? R(pc) : null,
            Reason = reason,
            Count = count,
            Detail = detail,
        });
        while (_events.Count > MaxEvents) _events.Dequeue();
        if (code is "error" or "leftover_cleared" or "paused" or "resumed" or "app_enabled" or "app_disabled")
            _log($"event {code}" + (app is null ? "" : " app=" + app) + (count is null ? "" : $" count={count}") + (detail is null ? "" : " " + detail));
    }

    // ------------------------------------------------------------------ persistence

    private LearnedProfiles LoadProfiles()
    {
        try
        {
            string? json = File.Exists(_paths.LearnedPath) ? File.ReadAllText(_paths.LearnedPath) : null;
            var p = LearnedProfiles.FromJson(json, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (p.Count > 0) _log($"learned profiles: {p.Count} loaded");
            return p;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("learned profiles: cannot read: " + ex.Message);
            return new LearnedProfiles();
        }
    }

    private void SaveProfiles()
    {
        try
        {
            string tmp = _paths.LearnedPath + ".tmp";
            File.WriteAllText(tmp, _profiles.ToJson());
            File.Move(tmp, _paths.LearnedPath, overwrite: true);
            _profilesDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("learned profiles: cannot save: " + ex.Message);
        }
    }

    private StreamWriter? OpenCsv()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDir);
            foreach (var f in new DirectoryInfo(_paths.DataDir).GetFiles("metrics_*.csv").OrderByDescending(f => f.CreationTimeUtc).Skip(19))
                f.Delete();
            string path = Path.Combine(_paths.DataDir, $"metrics_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.csv");
            var w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            w.WriteLine("time,app,pid,exe,role,state,ws_mb,private_ws_mb,commit_mb,cap_mb,learned_mb,floor_mb,faults_per_s,hard_per_s,cpu_mcyc_per_s,reclaimed_mb,avail_mb,store_mb,pressure,aggressiveness,store_ratio");
            return w;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("metrics: cannot open CSV: " + ex.Message);
            return null;
        }
    }

    private void WriteCsv()
    {
        if (_csv is null) return;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            string time = DateTime.Now.ToString("HH:mm:ss", inv);
            foreach (var tp in _procs.Values)
            {
                if (tp.Excluded) continue;
                double dt = Math.Max(tp.DtSeconds, 0.001);
                _csv.WriteLine(string.Join(',',
                    time, tp.AppExe, tp.Pid.ToString(inv), tp.Exe, tp.Role, tp.State.State.ToString(),
                    AppInfo.Mb(tp.Last.WorkingSet), AppInfo.Mb(tp.Last.PrivateWorkingSet), AppInfo.Mb(tp.Last.PrivateBytes),
                    AppInfo.Mb(tp.State.CapBytes), AppInfo.Mb(tp.State.LearnedCap), AppInfo.Mb(tp.State.FloorBytes),
                    (tp.FaultDelta / dt).ToString("F1", inv), (tp.HardDelta / dt).ToString("F1", inv),
                    (tp.CyclesPerSec / 1e6).ToString("F0", inv), AppInfo.Mb(tp.Reclaimed),
                    AppInfo.Mb(_mem.AvailPhys), AppInfo.Mb(_compression), Governor.Name(_governor.Level),
                    _governor.Aggressiveness.ToString("F2", inv), _storeRatio.Ratio.ToString("F2", inv)));
            }
        }
        catch (IOException ex)
        {
            _log("metrics: write failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ shutdown

    /// <summary>Releases every cap. Safe to call more than once; never throws. Main thread only.</summary>
    public bool ReleaseAll()
    {
        bool allOk = true;
        long now = Environment.TickCount64;
        foreach (var tp in _procs.Values)
        {
            try
            {
                bool kernelCapped = tp.State.IsCapped || tp.Handle.HardMaxActive();
                Controller.Shutdown(tp.State, now);
                tp.Savings.End();
                if (kernelCapped && tp.Handle.IsAlive && !tp.Handle.ClearHardMax(tp.Last.WorkingSet))
                {
                    allOk = false;
                    _log($"shutdown: FAILED to release {tp.Describe()}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                allOk = false;
                _log($"shutdown: error releasing {tp.Describe()}: {ex.Message}");
            }
        }
        if (allOk) _journal.Delete();
        if (_profilesDirty) SaveProfiles();
        return allOk;
    }

    public void Dispose()
    {
        foreach (var tp in _procs.Values) tp.Dispose();
        _procs.Clear();
        _memory.Dispose();
        _guardian.Dispose();
        try { _csv?.Dispose(); }
        catch (IOException) { }
        Wake.Dispose();
    }
}
