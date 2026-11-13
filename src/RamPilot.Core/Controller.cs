namespace RamPilot.Core;

/// <summary>Per-process controller state.</summary>
public enum CapState
{
    /// <summary>No cap.</summary>
    Free,
    /// <summary>Cap is being lowered step by step while the estimated stall cost stays under budget.</summary>
    Descending,
    /// <summary>Cap is held at the learned level.</summary>
    Holding,
}

public enum ActionKind { None, StartDescent, Lower, Raise, RestoreLearned, Reapply, Release }

public enum ReleaseReason { None, Focus, Active, Audio, Paused, Disabled, PressureLow, Error, Shutdown }

public enum RaiseReason { None, Refaults, Stall }

/// <summary>What the engine must do to one process this tick.</summary>
public readonly record struct ControllerAction(
    ActionKind Kind, ulong CapBytes = 0, ulong PrevCapBytes = 0,
    ReleaseReason Release = ReleaseReason.None, RaiseReason Raise = RaiseReason.None)
{
    public static ControllerAction None => default;

    /// <summary>True if the engine must apply <see cref="CapBytes"/> as a hard working-set maximum.</summary>
    public bool AppliesCap => Kind is ActionKind.StartDescent or ActionKind.Lower or ActionKind.Raise
        or ActionKind.RestoreLearned or ActionKind.Reapply;

    /// <summary>True if this action can evict pages (a new or lower cap).</summary>
    public bool Trims => Kind is ActionKind.StartDescent or ActionKind.Lower or ActionKind.RestoreLearned;
}

/// <summary>One fixed-cadence measurement of a process. <see cref="Valid"/> = false means "no usable deltas".</summary>
public readonly record struct ProcessSample
{
    public bool Valid { get; init; }
    public double DtSeconds { get; init; }
    public ulong WorkingSet { get; init; }
    public ulong PrivateWorkingSet { get; init; }
    public ulong CommitBytes { get; init; }
    /// <summary>All page faults since the previous sample (soft + hard, includes demand-zero).</summary>
    public uint FaultDelta { get; init; }
    public uint HardFaultDelta { get; init; }
    public double CyclesPerSec { get; init; }
}

/// <summary>Everything outside the process itself that influences a decision.</summary>
public readonly record struct DecisionContext
{
    /// <summary>Monotonic milliseconds.</summary>
    public long NowMs { get; init; }
    /// <summary>Wall clock (Unix seconds), only used for persisted floors.</summary>
    public long NowUnix { get; init; }
    public bool Paused { get; init; }
    public bool Disabled { get; init; }
    /// <summary>The process belongs to the app that owns the foreground window.</summary>
    public bool Focused { get; init; }
    /// <summary>The app has an active audio session (playing or capturing).</summary>
    public bool Audio { get; init; }
    /// <summary>Last time the app was in the foreground (monotonic ms).</summary>
    public long LastAppFocusMs { get; init; }
    public double Aggressiveness { get; init; }
    /// <summary>Post-resume or startup grace: hold everything, take no cost-based decisions.</summary>
    public bool Grace { get; init; }
    /// <summary>The kernel still reports our hard maximum (false = the target cleared it itself).</summary>
    public bool CapIntact { get; init; }
    /// <summary>Time since the process started (ms). Young processes are not capped: see PolicyConfig.MinProcessAgeSeconds.</summary>
    public long ProcessAgeMs { get; init; }
}

/// <summary>Mutable per-process state owned by the engine, mutated only by <see cref="Controller"/>.</summary>
public sealed class ControllerState
{
    public CapState State { get; set; }
    public ulong CapBytes { get; set; }
    public long StateSinceMs { get; set; }
    public long ActiveUntilMs { get; set; }
    public long AudioUntilMs { get; set; }
    public long RetryAfterMs { get; set; }
    /// <summary>Last time the process was busy: focused, under sustained CPU load, or playing/capturing audio (monotonic ms).
    /// Short CPU bursts do not count.</summary>
    public long LastBusyMs { get; set; }
    /// <summary>EWMA (0..1) of the share of recent time spent above the CPU threshold; see Controller.UpdateBusyFraction.</summary>
    public double BusyFraction { get; set; }
    /// <summary>The process has had a cap at least once in its lifetime (the first-cap idle rule applies until then).</summary>
    public bool EverCapped { get; set; }
    /// <summary>The app's own fault rate while uncapped and idle (demand-zero faults of new allocations etc.).</summary>
    public double FaultBaselinePerSec { get; set; }
    public int BaselineSamples { get; set; }
    /// <summary>Times the target removed our hard limit within <see cref="Controller.TamperWindowMs"/>.</summary>
    public int TamperCount { get; set; }
    public long LastTamperMs { get; set; }

    /// <summary>Working set when the last fresh descent started; the depth limit is relative to it.</summary>
    public ulong ReferenceWs { get; set; }
    /// <summary>While walking down to a learned cap, the cap to reach with fast steps (0 = none).</summary>
    public ulong FastTarget { get; set; }
    /// <summary>Aggressiveness when the cap started holding; a clear rise resumes the descent.</summary>
    public double HoldAggressiveness { get; set; }

    /// <summary>Last cap that survived a full window under budget (0 = nothing learned yet).</summary>
    public ulong LearnedCap { get; set; }
    /// <summary>ssthresh-style floor: a cap at FloorBytes/FloorMargin once cost too much.</summary>
    public ulong FloorBytes { get; set; }
    public long FloorSetUnix { get; set; }
    /// <summary>Set when LearnedCap or FloorBytes changed; the engine persists and clears it.</summary>
    public bool LearnedDirty { get; set; }

    // Cost window
    public double WindowSeconds { get; set; }
    public double WindowCostUs { get; set; }
    public long CleanSinceMs { get; set; }

    public bool IsCapped => State != CapState.Free;
}

/// <summary>
/// The policy. A pure function of (state, sample, config, context): no clocks, no I/O, no P/Invoke.
///
/// Free → Descending: only after FirstCapIdleSeconds of idleness (the kernel picks victims by working-set insertion
/// order until its aging data exists, experiments/README.md, probe A), and the first cap is WS × StartRatio (0.9),
/// never a slam. Every StepSeconds the window's estimated stall cost is compared to the budget:
/// (faults at cap − the app's own uncapped fault baseline) × RefaultCostUs + hard faults × HardFaultCostUs,
/// divided by window length. Cost ≤ budget × headroom → step down by StepFactor (scaled by aggressiveness), never
/// below max(MinCap, floor, depth limit). Cost above budget → Raise. When no further step is possible → Holding.
///
/// Raise (fast out): cap × IncreaseFactor, and the floor becomes the failing cap × FloorMargin, so the next descent
/// stops above the level that hurt (TCP ssthresh). The floor expires after FloorTtlHours and is persisted.
///
/// Re-entry after focus/activity/audio restores the learned cap instead of probing again: the first restore step
/// trims at most (1 − StartRatio) of the current WS, the rest is walked down with fast, cost-checked steps.
/// </summary>
public static class Controller
{
    /// <summary>Cap changes smaller than this are not worth a system call.</summary>
    public const ulong MinStepBytes = 1UL << 20;

    public static ControllerAction Decide(ControllerState s, in ProcessSample x, PolicyConfig p, in DecisionContext c)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(p);
        long now = c.NowMs;

        // 1. Unconditional releases. These run even without a valid sample (focus events, pause).
        if (c.Paused) return Release(s, now, ReleaseReason.Paused);
        if (c.Disabled) return Release(s, now, ReleaseReason.Disabled);
        if (c.Focused)
        {
            s.LastBusyMs = now;
            return Release(s, now, ReleaseReason.Focus);
        }
        if (c.Audio)
        {
            s.LastBusyMs = now;
            s.AudioUntilMs = now + p.AudioHoldMs;
            return Release(s, now, ReleaseReason.Audio);
        }
        // Activity: only SUSTAINED CPU load counts. Background chat apps run short notification/sync bursts every few
        // minutes (measured: 10-30 s bursts every 2-3 min on idle WhatsApp, docs/DESIGN.md). Those bursts touch the
        // hot set, not the cold pages, so they must neither release a cap nor reset the first-cap idle clock; if a
        // burst does need evicted pages, the refault-cost rule raises the cap within one tick.
        bool burst = x.Valid && x.CyclesPerSec > p.ActiveCyclesPerSec;
        if (x.Valid && !c.Grace) UpdateBusyFraction(s, burst, x.DtSeconds, p);
        if (x.Valid && !c.Grace && s.BusyFraction >= p.SustainedActiveFraction)
        {
            s.LastBusyMs = now;
            s.ActiveUntilMs = now + p.ActiveHoldMs;
            return Release(s, now, ReleaseReason.Active);
        }

        if (s.State == CapState.Free)
        {
            if (!x.Valid || c.Grace) return ControllerAction.None;
            if (now < s.ActiveUntilMs || now < s.AudioUntilMs || now < s.RetryAfterMs) return ControllerAction.None;
            if (now - c.LastAppFocusMs < p.FocusGraceMs) return ControllerAction.None;
            // Uncapped and idle (no focus grace, activity or audio hold): this is the app's own fault rate.
            // Burst samples are excluded so a notification does not inflate the baseline.
            if (!burst) UpdateFaultBaseline(s, x);
            if (c.ProcessAgeMs < p.MinProcessAgeMs) return ControllerAction.None;
            // First cap of this process: its pages must have been idle long enough for the working-set manager to
            // age them, otherwise the first trim evicts by insertion order and hits the hot set.
            if (!s.EverCapped && now - Math.Max(s.LastBusyMs, c.LastAppFocusMs) < p.FirstCapIdleMs) return ControllerAction.None;
            return Enter(s, x, p, c);
        }

        // 2. Capped.
        if (!c.CapIntact) return OnTampered(s, x, p, c);
        if (!x.Valid || c.Grace) return ControllerAction.None;
        DecayFaultBaseline(s, x.DtSeconds);
        return Control(s, x, p, c);
    }

    /// <summary>
    /// Exponentially weighted fraction of recent time the process was over the CPU threshold, time constant
    /// <see cref="PolicyConfig.ActiveWindowSeconds"/>. A 30 s burst from rest reaches 1 − e^(−0.5) ≈ 0.39 (below the
    /// default 0.5); continuous load crosses 0.5 after about 0.69 × window ≈ 42 s.
    /// </summary>
    public static void UpdateBusyFraction(ControllerState s, bool busy, double dtSeconds, PolicyConfig p)
    {
        if (dtSeconds <= 0) return;
        double alpha = 1 - Math.Exp(-dtSeconds / Math.Max(1, p.ActiveWindowSeconds));
        s.BusyFraction += alpha * ((busy ? 1.0 : 0.0) - s.BusyFraction);
    }

    /// <summary>Tamper counter window, and how often within it a target may clear our limit before we give up.</summary>
    public const long TamperWindowMs = 10 * 60_000;
    public const int MaxTampers = 3;
    /// <summary>After giving up, leave the process alone this long.</summary>
    public const long TamperBackoffMs = 30 * 60_000;

    /// <summary>
    /// The target removed our hard limit itself (its own SetProcessWorkingSetSize). Re-apply, but never as a large
    /// trim: if the working set regrew, re-apply at most one gentle step below it and walk down again. If the app keeps
    /// clearing the limit, stop fighting it and back off.
    /// </summary>
    private static ControllerAction OnTampered(ControllerState s, in ProcessSample x, PolicyConfig p, in DecisionContext c)
    {
        long now = c.NowMs;
        s.TamperCount = now - s.LastTamperMs > TamperWindowMs ? 1 : s.TamperCount + 1;
        s.LastTamperMs = now;
        if (s.TamperCount >= MaxTampers)
        {
            var r = Release(s, now, ReleaseReason.Error);
            s.RetryAfterMs = now + TamperBackoffMs;
            s.TamperCount = 0;
            return r;
        }
        ulong prev = s.CapBytes;
        ulong cap = Math.Max(prev, Units.Scale(x.WorkingSet, p.StartRatio));
        if (cap > prev + MinStepBytes)
        {
            s.CapBytes = cap;
            s.FastTarget = prev;
            SetState(s, CapState.Descending, c);
        }
        return new ControllerAction(ActionKind.Reapply, s.CapBytes, prev);
    }

    /// <summary>Refaults of one tick: faults at the cap minus what the app faults anyway when uncapped.</summary>
    public static double Refaults(ControllerState s, in ProcessSample x, PolicyConfig p)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(p);
        bool atCap = x.WorkingSet >= Units.Scale(s.CapBytes, p.AtCapRatio);
        if (!atCap) return 0;
        return Math.Max(0, x.FaultDelta - s.FaultBaselinePerSec * Math.Max(x.DtSeconds, 0));
    }

    /// <summary>Upper bound for the baseline, so a noisy uncapped period cannot hide real refaults.</summary>
    public const double MaxFaultBaselinePerSec = 500;

    /// <summary>Half-life of the baseline while capped: a stale baseline must not hide refaults for long.</summary>
    public const double BaselineHalfLifeSeconds = 600;

    private static void DecayFaultBaseline(ControllerState s, double dt)
    {
        if (dt <= 0 || s.FaultBaselinePerSec <= 0) return;
        s.FaultBaselinePerSec *= Math.Pow(0.5, dt / BaselineHalfLifeSeconds);
    }

    /// <summary>EWMA of the uncapped, idle fault rate (alpha 0.1, first sample taken as is, bounded).</summary>
    private static void UpdateFaultBaseline(ControllerState s, in ProcessSample x)
    {
        if (x.DtSeconds <= 0) return;
        double rate = Math.Min(x.FaultDelta / x.DtSeconds, MaxFaultBaselinePerSec);
        s.FaultBaselinePerSec = s.BaselineSamples == 0 ? rate : s.FaultBaselinePerSec * 0.9 + rate * 0.1;
        if (s.BaselineSamples < int.MaxValue) s.BaselineSamples++;
    }

    /// <summary>The engine could not apply an action: forget the cap and back off for a minute.</summary>
    public static void OnApplyFailed(ControllerState s, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(s);
        SetFree(s, nowMs);
        s.RetryAfterMs = nowMs + 60_000;
    }

    /// <summary>Releases unconditionally (engine shutdown, process exit). Returns the action for symmetry.</summary>
    public static ControllerAction Shutdown(ControllerState s, long nowMs) => Release(s, nowMs, ReleaseReason.Shutdown);

    /// <summary>Floor that is still in force at <paramref name="nowUnix"/> (0 if none or expired).</summary>
    public static ulong EffectiveFloor(ControllerState s, PolicyConfig p, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(p);
        if (s.FloorBytes == 0) return 0;
        return nowUnix - s.FloorSetUnix < (long)(p.FloorTtlHours * 3600) ? s.FloorBytes : 0;
    }

    /// <summary>How deep the governor lets a cap go: reference WS × (1 − aggressiveness × MaxDepth).</summary>
    public static ulong DepthLimit(ulong referenceWs, PolicyConfig p, double aggressiveness)
    {
        ArgumentNullException.ThrowIfNull(p);
        double a = Math.Clamp(aggressiveness, 0, 1);
        return Units.Scale(referenceWs, Math.Max(0, 1 - a * p.MaxDepth));
    }

    /// <summary>Step factor scaled by aggressiveness: full step at 1, half step at 0.</summary>
    public static double EffectiveStepFactor(PolicyConfig p, double aggressiveness)
    {
        ArgumentNullException.ThrowIfNull(p);
        double a = Math.Clamp(aggressiveness, 0, 1);
        return 1 - (1 - p.StepFactor) * (0.5 + 0.5 * a);
    }

    /// <summary>Stall budget as a fraction of wall time; more stall is acceptable when the system needs memory.</summary>
    public static double Budget(PolicyConfig p, double aggressiveness)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.StallBudget * (0.5 + Math.Clamp(aggressiveness, 0, 1));
    }

    /// <summary>Lowest cap allowed right now.</summary>
    public static ulong LowerBound(ControllerState s, PolicyConfig p, in DecisionContext c)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(p);
        return Max(p.MinCap, EffectiveFloor(s, p, c.NowUnix), DepthLimit(s.ReferenceWs, p, c.Aggressiveness));
    }

    private static ControllerAction Enter(ControllerState s, in ProcessSample x, PolicyConfig p, in DecisionContext c)
    {
        ulong ws = x.WorkingSet;
        if (ws < p.MinWsToManage) return ControllerAction.None;
        ulong floor = Math.Max(p.MinCap, EffectiveFloor(s, p, c.NowUnix));
        ulong startCap = Units.Scale(ws, p.StartRatio);

        // Re-entry with knowledge: restore the learned cap, do not probe again.
        if (s.LearnedCap > 0 && s.ReferenceWs > 0)
        {
            // Whenever the app has regrown above what it is known to tolerate, go back to that level, but never by
            // more than one gentle step at once: the rest is walked down with fast, cost-checked steps.
            // The depth limit is relative to the current WS, so a stored reference cannot bypass the governor.
            ulong target = Max(s.LearnedCap, floor, DepthLimit(ws, p, c.Aggressiveness));
            if (target + MinStepBytes < ws)
            {
                s.ReferenceWs = ws;
                ulong cap = Math.Max(target, startCap);
                if (cap + MinStepBytes >= ws) return ControllerAction.None;
                s.CapBytes = cap;
                s.EverCapped = true;
                s.FastTarget = cap > target + MinStepBytes ? target : 0;
                SetState(s, s.FastTarget > 0 ? CapState.Descending : CapState.Holding, c);
                return new ControllerAction(ActionKind.RestoreLearned, cap);
            }
            // The app shrank below what we learned; fall through to a fresh, gentle descent.
        }

        s.ReferenceWs = ws;
        ulong limit = Math.Max(floor, DepthLimit(ws, p, c.Aggressiveness));
        ulong first = Math.Max(startCap, limit);
        if (first + MinStepBytes >= ws) return ControllerAction.None;   // nothing worth doing at this pressure
        s.CapBytes = first;
        s.EverCapped = true;
        s.FastTarget = 0;
        SetState(s, CapState.Descending, c);
        return new ControllerAction(ActionKind.StartDescent, first);
    }

    private static ControllerAction Control(ControllerState s, in ProcessSample x, PolicyConfig p, in DecisionContext c)
    {
        double dt = Math.Max(x.DtSeconds, 0.001);
        // PageFaultCount counts every refault exactly once, from standby, store or disk; HardFaultCount sees only
        // disk reads (never store reads), so it is a separate disk-I/O term, not the refault signal.
        double refaultUs = Refaults(s, x, p) * p.RefaultCostUs;
        double hardUs = x.HardFaultDelta * p.HardFaultCostUs;
        double tickCostUs = refaultUs + hardUs;
        double budget = Budget(p, c.Aggressiveness);
        var reason = hardUs > refaultUs ? RaiseReason.Stall : RaiseReason.Refaults;

        if (tickCostUs / 1e6 / dt > budget * p.SpikeMultiple) return Raise(s, x, p, c, reason);

        s.WindowCostUs += tickCostUs;
        s.WindowSeconds += dt;
        bool fast = s.State == CapState.Descending && s.FastTarget > 0 && s.CapBytes > s.FastTarget;
        double interval = fast ? p.FastStepSeconds : p.StepSeconds;
        if (s.WindowSeconds < interval) return ControllerAction.None;

        double frac = s.WindowCostUs / 1e6 / s.WindowSeconds;
        s.WindowCostUs = 0;
        s.WindowSeconds = 0;
        if (frac > budget) return Raise(s, x, p, c, reason);
        bool clean = frac <= budget * p.DescendHeadroom;
        if (!clean) s.CleanSinceMs = c.NowMs;

        ulong limit = LowerBound(s, p, c);
        if (s.State == CapState.Descending)
        {
            // The cap we are leaving survived a full window under budget: it is the last known-good cap.
            // While walking back down to a learned cap, keep the (lower) learned value instead.
            if (!fast) Learn(s, s.CapBytes);
            if (!clean)
            {
                SetState(s, CapState.Holding, c);
                return ControllerAction.None;
            }
            ulong next = Units.Scale(s.CapBytes, EffectiveStepFactor(p, c.Aggressiveness));
            if (fast) next = Math.Max(next, s.FastTarget);
            else s.FastTarget = 0;
            next = Math.Max(next, limit);
            if (next + MinStepBytes > s.CapBytes)
            {
                s.FastTarget = 0;
                SetState(s, CapState.Holding, c);
                return ControllerAction.None;
            }
            ulong prev = s.CapBytes;
            s.CapBytes = next;
            if (s.FastTarget > 0 && next <= s.FastTarget)
            {
                // Back at the learned cap: hold there, as before the interruption.
                s.FastTarget = 0;
                SetState(s, CapState.Holding, c);
            }
            return new ControllerAction(ActionKind.Lower, next, prev);
        }

        // Holding.
        Learn(s, s.CapBytes);
        bool roomBelow = Units.Scale(s.CapBytes, EffectiveStepFactor(p, c.Aggressiveness)) >= limit + MinStepBytes;
        bool pressureRose = c.Aggressiveness > s.HoldAggressiveness + 0.1;
        bool retestDue = c.NowMs - s.CleanSinceMs >= p.HoldRetestMs;
        if (clean && roomBelow && (pressureRose || retestDue))
            SetState(s, CapState.Descending, c);
        return ControllerAction.None;
    }

    private static ControllerAction Raise(ControllerState s, in ProcessSample x, PolicyConfig p, in DecisionContext c, RaiseReason reason)
    {
        ulong prev = s.CapBytes;
        ulong floor = Math.Max(EffectiveFloor(s, p, c.NowUnix), Units.Scale(prev, p.FloorMargin));
        ulong next = Max(Units.Scale(prev, p.IncreaseFactor), floor, p.MinCap);
        if (s.FloorBytes != floor || s.FloorSetUnix != c.NowUnix)
        {
            s.FloorBytes = floor;
            s.FloorSetUnix = c.NowUnix;
            s.LearnedDirty = true;
        }
        s.CapBytes = next;
        s.FastTarget = 0;
        s.WindowCostUs = 0;
        s.WindowSeconds = 0;
        Learn(s, next);
        SetState(s, CapState.Holding, c);
        return new ControllerAction(ActionKind.Raise, next, prev, Raise: reason);
    }

    private static ControllerAction Release(ControllerState s, long now, ReleaseReason reason)
    {
        if (s.State == CapState.Free) return ControllerAction.None;
        ulong prev = s.CapBytes;
        SetFree(s, now);
        return new ControllerAction(ActionKind.Release, 0, prev, Release: reason);
    }

    private static void SetFree(ControllerState s, long now)
    {
        s.State = CapState.Free;
        s.CapBytes = 0;
        s.FastTarget = 0;
        s.StateSinceMs = now;
        s.WindowCostUs = 0;
        s.WindowSeconds = 0;
    }

    private static void SetState(ControllerState s, CapState state, in DecisionContext c)
    {
        if (s.State != state) s.StateSinceMs = c.NowMs;
        if (state == CapState.Holding && s.State != CapState.Holding)
        {
            s.HoldAggressiveness = c.Aggressiveness;
            s.CleanSinceMs = c.NowMs;
        }
        if (s.State == CapState.Free)
        {
            s.WindowCostUs = 0;
            s.WindowSeconds = 0;
            s.CleanSinceMs = c.NowMs;
        }
        s.State = state;
    }

    private static void Learn(ControllerState s, ulong cap)
    {
        if (s.LearnedCap == cap) return;
        s.LearnedCap = cap;
        s.LearnedDirty = true;
    }

    private static ulong Max(ulong a, ulong b, ulong c) => Math.Max(a, Math.Max(b, c));
}
