using RamPilot.Core;

namespace RamPilot.Core.Tests;

public class ControllerTests
{
    private static ulong Mb(double v) => Units.FromMb(v);

    private static Harness Idle(double aggressiveness = 1.0, int hot = 40, int gc = 60, int cold = 300, double gcPeriod = 120)
    {
        var h = new Harness(new SimulatedProcess(hot, gc, cold, gcPeriod), aggressiveness: aggressiveness);
        h.LastFocusMs = h.NowMs - 10 * 60_000;   // last focused long ago
        return h;
    }

    [Fact]
    public void FirstCapIsAFractionOfTheWorkingSetNotASlam()
    {
        var h = Idle();
        h.Run(10);
        var first = h.Log.First(l => l.A.AppliesCap);
        Assert.Equal(ActionKind.StartDescent, first.A.Kind);
        Assert.True(first.A.CapBytes >= Units.Scale(first.WsBefore, h.Policy.StartRatio) - Units.MiB,
            $"first cap {Units.ToMb(first.A.CapBytes)} MB vs WS {Units.ToMb(first.WsBefore)} MB");
    }

    [Fact]
    public void DescentIsGradualEveryTrimIsBounded()
    {
        var h = Idle();
        h.Run(3 * 3600);
        foreach (var (_, a, ws, _) in h.Log)
        {
            switch (a.Kind)
            {
                case ActionKind.StartDescent:
                    Assert.True(a.CapBytes + Units.MiB >= Units.Scale(ws, h.Policy.StartRatio));
                    break;
                case ActionKind.Lower:
                    Assert.True(a.CapBytes + Units.MiB >= Units.Scale(a.PrevCapBytes, h.Policy.StepFactor),
                        $"step {Units.ToMb(a.PrevCapBytes)} -> {Units.ToMb(a.CapBytes)} MB");
                    Assert.True(a.CapBytes < a.PrevCapBytes);
                    break;
                case ActionKind.RestoreLearned:
                    Assert.True(a.CapBytes + Units.MiB >= Units.Scale(ws, h.Policy.StartRatio));
                    break;
            }
        }
        Assert.True(h.Count(ActionKind.Lower) >= 3, "expected several descent steps");
    }

    [Fact]
    public void ConvergesAboveTheGcWorkingSetWithoutSawtooth()
    {
        // hot 40 MB + GC region 60 MB: the real need is ~100 MB, cold 300 MB should go.
        var h = Idle();
        h.Run(4 * 3600);
        int raises = h.Count(ActionKind.Raise);
        Assert.InRange(raises, 1, 5);
        // After learning, the last hour has no raises at all.
        long lastHour = h.NowMs - 3_600_000;
        Assert.DoesNotContain(h.Log, l => l.T > lastHour && l.A.Kind == ActionKind.Raise);
        Assert.True(h.State.CapBytes >= Mb(100), $"cap {Units.ToMb(h.State.CapBytes)} MB");
        Assert.True(h.State.CapBytes <= Mb(200), $"cap {Units.ToMb(h.State.CapBytes)} MB");
        Assert.True(h.Sim.ResidentMb < 220, $"resident {h.Sim.ResidentMb} MB of {h.Sim.TotalMb}");
    }

    [Fact]
    public void RaiseSetsFloorAndLaterDescentsStopAboveIt()
    {
        var h = Idle();
        h.Run(4 * 3600);
        var raise = h.Log.First(l => l.A.Kind == ActionKind.Raise);
        ulong floorAfterFirstRaise = Units.Scale(raise.A.PrevCapBytes, h.Policy.FloorMargin);
        foreach (var l in h.Log.Where(l => l.T > raise.T && l.A.AppliesCap))
            Assert.True(l.A.CapBytes + Units.MiB >= floorAfterFirstRaise);
    }

    [Fact]
    public void NeverCapsBelowFloorOrMinimum()
    {
        foreach (double aggr in new[] { 0.0, 0.4, 0.75, 1.0 })
        {
            var h = Idle(aggr, hot: 20, gc: 30, cold: 200, gcPeriod: 90);
            h.Run(3 * 3600);
            foreach (var l in h.Log.Where(l => l.A.AppliesCap))
            {
                Assert.True(l.A.CapBytes >= h.Policy.MinCap);
                Assert.True(l.A.CapBytes >= l.Floor, $"cap {Units.ToMb(l.A.CapBytes)} < floor {Units.ToMb(l.Floor)}");
            }
        }
    }

    [Fact]
    public void LowPressureDoesNotDescendDeep()
    {
        var h = Idle(aggressiveness: 0.4);
        h.Run(2 * 3600);
        ulong limit = Controller.DepthLimit(h.State.ReferenceWs, h.Policy, 0.4);
        Assert.True(h.State.IsCapped);
        Assert.True(h.State.CapBytes + Units.MiB >= limit);
        Assert.True(h.State.CapBytes >= Units.Scale(h.State.ReferenceWs, 0.55));
        Assert.Equal(0, h.Count(ActionKind.Raise));
    }

    [Fact]
    public void ZeroAggressivenessMeansNoCap()
    {
        var h = Idle(aggressiveness: 0.0);
        h.Run(1800);
        Assert.DoesNotContain(h.Log, l => l.A.AppliesCap);
    }

    [Fact]
    public void RisingPressureResumesDescentFromHolding()
    {
        var h = Idle(aggressiveness: 0.4);
        h.Run(1800);
        Assert.Equal(CapState.Holding, h.State.State);
        ulong held = h.State.CapBytes;
        h.Aggressiveness = 1.0;
        h.Run(300);
        Assert.True(h.State.CapBytes < held, "higher pressure should allow a deeper cap");
    }

    [Fact]
    public void FocusedAppIsNeverCapped()
    {
        var h = Idle();
        var rng = new Random(7);
        for (int i = 0; i < 20_000; i++)
        {
            h.Focused = rng.NextDouble() < 0.05 ? !h.Focused : h.Focused;
            var a = h.Step();
            if (h.Focused)
            {
                Assert.False(a.AppliesCap);
                Assert.Equal(CapState.Free, h.State.State);
            }
        }
        Assert.True(h.Count(ActionKind.StartDescent) + h.Count(ActionKind.RestoreLearned) > 0);
    }

    [Fact]
    public void AudioAppIsNeverCappedAndHeldAfterwards()
    {
        var h = Idle();
        h.Run(600);
        Assert.True(h.State.IsCapped);
        h.Audio = true;
        var a = h.Step();
        Assert.Equal(ActionKind.Release, a.Kind);
        Assert.Equal(ReleaseReason.Audio, a.Release);
        for (int i = 0; i < 500; i++) Assert.False(h.Step().AppliesCap);
        h.Audio = false;
        int holdTicks = (int)(h.Policy.AudioHoldSeconds / h.Policy.TickSeconds) - 1;
        for (int i = 0; i < holdTicks; i++) Assert.False(h.Step().AppliesCap);
    }

    [Fact]
    public void FirstCapWaitsForTheDataBackedIdlePeriodAfterFocus()
    {
        // experiments/README.md, probe A: the first trim is only access-aware after ~300 s of idleness.
        var h = new Harness(new SimulatedProcess(40, 60, 300));
        Assert.True(h.Policy.FirstCapIdleSeconds >= 300);
        h.LastFocusMs = h.NowMs;
        h.Run(h.Policy.FirstCapIdleSeconds - 4);
        Assert.Empty(h.Log);
        h.Run(10);
        Assert.Contains(h.Log, l => l.A.Kind == ActionKind.StartDescent);
    }

    [Fact]
    public void FirstCapWaitsForTheIdlePeriodAfterCpuActivityAndAudio()
    {
        var h = Idle();
        h.State.LastBusyMs = h.NowMs;   // e.g. the engine just started tracking the process
        h.Run(h.Policy.FirstCapIdleSeconds - 30);
        Assert.Empty(h.Log);
        h.Audio = true;
        h.Step();
        h.Audio = false;
        h.Run(h.Policy.FirstCapIdleSeconds - 10);
        Assert.Empty(h.Log);
        h.Run(20);
        Assert.Contains(h.Log, l => l.A.Kind == ActionKind.StartDescent);

        // Sustained CPU load (not a short burst) also restarts the idle clock before the first cap.
        var h2 = Idle();
        h2.State.LastBusyMs = h2.NowMs;   // first sight: not capped yet
        BurstFor(h2, 90, 0);
        Assert.True(h2.State.LastBusyMs > 0, "90 s of continuous load must count as sustained activity");
        long busyEnd = h2.State.LastBusyMs;
        h2.Run(h2.Policy.FirstCapIdleSeconds - 30);
        Assert.DoesNotContain(h2.Log, l => l.A.AppliesCap);
        h2.Run(60);
        var first = h2.Log.First(l => l.A.AppliesCap);
        Assert.Equal(ActionKind.StartDescent, first.A.Kind);
        Assert.True(first.T - busyEnd >= h2.Policy.FirstCapIdleMs);
    }

    [Fact]
    public void ShortBurstsDoNotBlockTheFirstCapNorReleaseACap()
    {
        // Measured on idle WhatsApp: 10-30 s notification/sync bursts every 2-3 minutes. With the old
        // "any busy sample is activity" rule the first cap never happened.
        var h = Idle();
        h.State.LastBusyMs = h.NowMs;   // first sight
        for (int i = 0; i < 6; i++)     // 15 minutes: a 20 s burst every 150 s
        {
            BurstFor(h, 20, 0);
            h.Run(130);
        }
        Assert.Contains(h.Log, l => l.A.Kind == ActionKind.StartDescent);
        Assert.DoesNotContain(h.Log, l => l.A.Kind == ActionKind.Release && l.A.Release == ReleaseReason.Active);
        Assert.True(h.State.IsCapped);
        Assert.True(h.State.BusyFraction < h.Policy.SustainedActiveFraction);
    }

    [Fact]
    public void SustainedLoadReleasesWithinAMinute()
    {
        var h = Idle();
        h.Run(3600);
        Assert.True(h.State.IsCapped);
        int before = h.Log.Count;
        BurstFor(h, 60, 0);
        var release = h.Log.Skip(before).FirstOrDefault(l => l.A.Kind == ActionKind.Release);
        Assert.Equal(ReleaseReason.Active, release.A.Release);
        Assert.InRange((release.T - h.Log[before - 1].T) / 1000.0, 0, 3600);   // released during the load
        Assert.False(h.State.IsCapped);
    }

    [Fact]
    public void BusyFractionMath()
    {
        var p = new PolicyConfig();
        var s = new ControllerState();
        for (int i = 0; i < 15; i++) Controller.UpdateBusyFraction(s, true, 2, p);   // 30 s burst
        Assert.InRange(s.BusyFraction, 0.38, 0.40);
        Assert.True(s.BusyFraction < p.SustainedActiveFraction);
        var s2 = new ControllerState();
        int ticks = 0;
        while (s2.BusyFraction < p.SustainedActiveFraction) { Controller.UpdateBusyFraction(s2, true, 2, p); ticks++; }
        Assert.InRange(ticks * 2, 40, 44);   // continuous load crosses the threshold after ~42 s
    }

    [Fact]
    public void FirstStepIsGentle()
    {
        var p = new PolicyConfig();
        Assert.True(p.StartRatio >= 0.9);
        var h = Idle();
        h.Run(30);
        var first = h.Log.Single(l => l.A.AppliesCap);
        Assert.Equal(ActionKind.StartDescent, first.A.Kind);
        Assert.True(first.A.CapBytes + Units.MiB >= Units.Scale(first.WsBefore, 0.9));
        Assert.True(first.A.CapBytes < first.WsBefore);
    }

    [Fact]
    public void UncappedFaultBaselineIsNotCountedAsRefaults()
    {
        var p = new PolicyConfig();
        var s = new ControllerState { State = CapState.Holding, CapBytes = Mb(100), FaultBaselinePerSec = 100 };
        var atCap = new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(100), FaultDelta = 200 };
        Assert.Equal(0, Controller.Refaults(s, atCap, p));
        Assert.Equal(1000, Controller.Refaults(s, atCap with { FaultDelta = 1200 }, p));
        Assert.Equal(0, Controller.Refaults(s, atCap with { WorkingSet = Mb(50), FaultDelta = 5000 }, p));

        // The baseline is learned while uncapped and idle (here: the 300 s before the first cap).
        var h = Idle();
        h.State.LastBusyMs = h.NowMs;
        h.Sim.BaseFaultsPerTick = 400;   // 200 faults/s of demand-zero allocations, with or without a cap
        h.Run(h.Policy.FirstCapIdleSeconds + 120);
        Assert.True(h.State.IsCapped);
        Assert.InRange(h.State.FaultBaselinePerSec, 150, 250);
        // Without the subtraction, 200 faults/s x 30 us = 0.6 % of wall time would exceed the budget on every window.
        var reference = Idle();
        reference.State.LastBusyMs = reference.NowMs;
        reference.Run(reference.Policy.FirstCapIdleSeconds + 120);
        Assert.True(h.Count(ActionKind.Raise) <= reference.Count(ActionKind.Raise),
            $"{h.Count(ActionKind.Raise)} raises with allocation faults vs {reference.Count(ActionKind.Raise)} without");
    }

    [Fact]
    public void BaselineDecaysWhileCapped()
    {
        var p = new PolicyConfig();
        var s = new ControllerState { State = CapState.Holding, CapBytes = Mb(200), FaultBaselinePerSec = 200, BaselineSamples = 10 };
        var x = new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(150) };
        long now = 10_000_000;
        int ticks = (int)(Controller.BaselineHalfLifeSeconds / 2);
        for (int i = 0; i < ticks; i++)
            Controller.Decide(s, x, p, new DecisionContext { NowMs = now + i * 2000L, CapIntact = true, ProcessAgeMs = 3_600_000, Aggressiveness = 1 });
        Assert.InRange(s.FaultBaselinePerSec, 99, 101);
    }

    [Fact]
    public void BaselineIsLearnedOnlyWhileIdle()
    {
        var p = new PolicyConfig();
        var s = new ControllerState { LastBusyMs = 0 };
        var x = new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(40), FaultDelta = 2000, CyclesPerSec = 1e6 };
        long now = 10_000_000;
        // focus grace: last focus 5 s ago
        Controller.Decide(s, x, p, new DecisionContext { NowMs = now, LastAppFocusMs = now - 5000, CapIntact = true, ProcessAgeMs = 3_600_000 });
        Assert.Equal(0, s.BaselineSamples);
        // a lone burst sample is not counted into the baseline
        Controller.Decide(s, x with { CyclesPerSec = 1e9 }, p, new DecisionContext { NowMs = now, CapIntact = true, ProcessAgeMs = 3_600_000 });
        Assert.Equal(0, s.BaselineSamples);
        // sustained activity, then inside the activity hold
        s.BusyFraction = 0.9;
        Controller.Decide(s, x with { CyclesPerSec = 1e9 }, p, new DecisionContext { NowMs = now + 2000, CapIntact = true, ProcessAgeMs = 3_600_000 });
        Controller.Decide(s, x, p, new DecisionContext { NowMs = now + 4000, CapIntact = true, ProcessAgeMs = 3_600_000 });
        Assert.Equal(0, s.BaselineSamples);
        // idle
        s.BusyFraction = 0;
        Controller.Decide(s, x, p, new DecisionContext { NowMs = now + 600_000, CapIntact = true, ProcessAgeMs = 3_600_000 });
        Assert.Equal(1, s.BaselineSamples);
        Assert.Equal(Controller.MaxFaultBaselinePerSec, s.FaultBaselinePerSec);   // 1000/s, bounded
    }

    [Fact]
    public void TamperIsReappliedGentlyThenGivenUp()
    {
        var p = new PolicyConfig();
        var s = new ControllerState { State = CapState.Holding, CapBytes = Mb(100), EverCapped = true };
        long now = 10_000_000;
        var ctx = new DecisionContext { NowMs = now, CapIntact = false, ProcessAgeMs = 3_600_000, Aggressiveness = 1 };

        // WS regrew to 300 MB after the target cleared the limit: re-apply one gentle step, then walk down.
        var a = Controller.Decide(s, new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(300) }, p, ctx);
        Assert.Equal(ActionKind.Reapply, a.Kind);
        Assert.Equal(Mb(270), a.CapBytes);
        Assert.Equal(Mb(100), s.FastTarget);
        Assert.Equal(CapState.Descending, s.State);

        // Small regrowth: same cap.
        var a2 = Controller.Decide(s, new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(270) }, p, ctx with { NowMs = now + 60_000 });
        Assert.Equal(ActionKind.Reapply, a2.Kind);
        Assert.Equal(Mb(270), a2.CapBytes);

        // Third time within 10 minutes: stop fighting the app, release and back off for 30 minutes.
        var a3 = Controller.Decide(s, new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(270) }, p, ctx with { NowMs = now + 120_000 });
        Assert.Equal(ActionKind.Release, a3.Kind);
        Assert.Equal(ReleaseReason.Error, a3.Release);
        Assert.False(s.IsCapped);
        var later = new DecisionContext { NowMs = now + 120_000 + Controller.TamperBackoffMs - 2000, CapIntact = true, ProcessAgeMs = 3_600_000, Aggressiveness = 1 };
        Assert.Equal(ActionKind.None, Controller.Decide(s, new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(300) }, p, later).Kind);
    }

    [Fact]
    public void RestoreUsesTheCurrentWorkingSetForTheDepthLimit()
    {
        // Stored reference 100 MB, app has grown to 400 MB. At low pressure (a = 0.4) the depth limit must be
        // 60 % of the CURRENT working set, not of the stored reference.
        var p = new PolicyConfig();
        var s = new ControllerState { LearnedCap = Mb(50), ReferenceWs = Mb(100), EverCapped = true };
        var x = new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(400) };
        var ctx = new DecisionContext { NowMs = 10_000_000, NowUnix = 2000, Aggressiveness = 0.4, CapIntact = true, ProcessAgeMs = 3_600_000 };
        var a = Controller.Decide(s, x, p, ctx);
        Assert.Equal(ActionKind.RestoreLearned, a.Kind);
        Assert.Equal(Mb(400), s.ReferenceWs);
        Assert.Equal(Mb(240), s.FastTarget);   // 0.6 x 400, not the learned 50
    }

    [Fact]
    public void SustainedActivityReleasesAndReentryIsGentleAndRespectsTheFloor()
    {
        var h = Idle();
        h.Run(3 * 3600);
        Assert.Equal(CapState.Holding, h.State.State);

        // Sustained activity (e.g. a large background sync). While still capped, the load faults pages back in, so
        // the cost rule raises the cap first and records a floor (ssthresh); ~42 s in, sustained activity releases.
        int before = h.Log.Count;
        BurstFor(h, 60, 40);
        var rel = h.Log.Skip(before).First(l => l.A.Kind == ActionKind.Release);
        Assert.Equal(ReleaseReason.Active, rel.A.Release);
        int released = h.Log.IndexOf(rel) + 1;
        ulong floorAtRelease = Math.Max(h.State.FloorBytes, h.Policy.MinCap);
        h.Run(900);

        // Re-entry: after the activity hold, one gentle step (learned restore, or a fresh descent when the app is
        // already below what it learned), and never below the floor the load taught it.
        var reentry = h.Log.Skip(released).First(l => l.A.AppliesCap);
        Assert.True(reentry.A.Kind is ActionKind.RestoreLearned or ActionKind.StartDescent, reentry.A.Kind.ToString());
        Assert.True(reentry.A.CapBytes + Units.MiB >= Units.Scale(reentry.WsBefore, h.Policy.StartRatio), "re-entry must be one gentle step");
        Assert.True(reentry.T - rel.T >= h.Policy.ActiveHoldMs);
        foreach (var l in h.Log.Skip(released).Where(l => l.A.AppliesCap))
            Assert.True(l.A.CapBytes + Units.MiB >= floorAtRelease, $"cap {Units.ToMb(l.A.CapBytes):F0} MB below floor {Units.ToMb(floorAtRelease):F0} MB");
        Assert.True(h.State.IsCapped);
    }

    [Fact]
    public void RestoreAfterLargeGrowthIsBoundedAndWalksDownFast()
    {
        var h = Idle();
        h.Run(3 * 3600);
        ulong learned = h.State.LearnedCap;
        int before = h.Log.Count;
        BurstFor(h, 60, 250);   // sustained load that also grows the app by 250 MB
        var rel = h.Log.Skip(before).First(l => l.A.Kind == ActionKind.Release);
        Assert.Equal(ReleaseReason.Active, rel.A.Release);
        int released = h.Log.IndexOf(rel) + 1;
        ulong ceiling = Math.Max(Math.Max(learned, h.State.LearnedCap), h.State.FloorBytes);
        h.Run(h.Policy.ActiveHoldSeconds + h.Policy.ActiveWindowSeconds);
        var restore = h.Log.Skip(released).First(l => l.A.AppliesCap);
        Assert.True(restore.A.CapBytes > learned, "large growth must not be trimmed in one step");
        Assert.True(restore.A.CapBytes + Units.MiB >= Units.Scale(restore.WsBefore, h.Policy.StartRatio));
        h.Run(1800);
        foreach (var l in h.Log.Skip(released).Where(l => l.A.Kind == ActionKind.Lower))
            Assert.True(l.A.CapBytes + Units.MiB >= Units.Scale(l.A.PrevCapBytes, h.Policy.StepFactor), "walk-down steps stay bounded");
        // ...and it walks back down to the level it knows (learned cap or the floor the load set), not stuck high.
        Assert.True(h.State.CapBytes <= Units.Scale(ceiling, 1.15),
            $"cap {Units.ToMb(h.State.CapBytes):F0} MB did not come back near {Units.ToMb(ceiling):F0} MB");
    }

    [Fact]
    public void LearnedCapAboveStartRatioIsStillRestoredInsteadOfANewDescent()
    {
        var p = new PolicyConfig();
        var s = new ControllerState { LearnedCap = Mb(240), ReferenceWs = Mb(200), FloorBytes = Mb(176), FloorSetUnix = 1000 };
        var x = new ProcessSample { Valid = true, DtSeconds = 2, WorkingSet = Mb(280), PrivateWorkingSet = Mb(270), CommitBytes = Mb(310) };
        var ctx = new DecisionContext { NowMs = 10_000_000, NowUnix = 2000, Aggressiveness = 0.75, CapIntact = true, ProcessAgeMs = 3_600_000 };
        var a = Controller.Decide(s, x, p, ctx);
        Assert.Equal(ActionKind.RestoreLearned, a.Kind);
        Assert.Equal(Mb(252), a.CapBytes);                 // one gentle step (0.9 x 280), not a jump to 240
        Assert.Equal(CapState.Descending, s.State);
        Assert.Equal(Mb(240), s.FastTarget);               // the rest is walked down with fast steps

        var s1 = new ControllerState { LearnedCap = Mb(240), ReferenceWs = Mb(200), FloorBytes = Mb(176), FloorSetUnix = 1000 };
        var a1 = Controller.Decide(s1, x with { WorkingSet = Mb(260) }, p, ctx);
        Assert.Equal(ActionKind.RestoreLearned, a1.Kind);
        Assert.Equal(Mb(240), a1.CapBytes);                // within one step: straight to the learned cap
        Assert.Equal(CapState.Holding, s1.State);

        // Smaller than what was learned: fresh, gentle descent from 0.9 x WS (but not below the floor).
        var s2 = new ControllerState { LearnedCap = Mb(240), ReferenceWs = Mb(200), FloorBytes = Mb(176), FloorSetUnix = 1000 };
        var a2 = Controller.Decide(s2, x with { WorkingSet = Mb(230) }, p, ctx);
        Assert.Equal(ActionKind.StartDescent, a2.Kind);
        Assert.Equal(Mb(207), a2.CapBytes);
    }

    [Fact]
    public void PauseDisableAndShutdownRelease()
    {
        var h = Idle(gc: 0, gcPeriod: 0);
        h.Run(300);
        Assert.True(h.State.IsCapped);
        h.Paused = true;
        var a = h.Step();
        Assert.Equal(ActionKind.Release, a.Kind);
        Assert.Equal(ReleaseReason.Paused, a.Release);
        h.Run(600);
        Assert.DoesNotContain(h.Log.Skip(h.Log.Count - 1), l => l.A.AppliesCap);
        Assert.False(h.State.IsCapped);

        h.Paused = false;
        h.Run(600);
        Assert.True(h.State.IsCapped);
        var ctx = new DecisionContext { NowMs = h.NowMs, Disabled = true, CapIntact = true };
        Assert.Equal(ReleaseReason.Disabled, Controller.Decide(h.State, h.Last, h.Policy, ctx).Release);

        var h2 = Idle(gc: 0, gcPeriod: 0);
        h2.Run(300);
        Assert.True(h2.State.IsCapped);
        var s = Controller.Shutdown(h2.State, h2.NowMs);
        Assert.Equal(ActionKind.Release, s.Kind);
        Assert.False(h2.State.IsCapped);
        Assert.Equal(ActionKind.None, Controller.Shutdown(h2.State, h2.NowMs).Kind);
    }

    [Fact]
    public void FocusReleaseWorksWithoutAValidSample()
    {
        var h = Idle();
        h.Run(300);
        Assert.True(h.State.IsCapped);
        var ctx = new DecisionContext { NowMs = h.NowMs, Focused = true, CapIntact = true };
        var a = Controller.Decide(h.State, default, h.Policy, ctx);
        Assert.Equal(ReleaseReason.Focus, a.Release);
    }

    [Fact]
    public void SpikeRaisesImmediately()
    {
        var h = Idle(hot: 40, gc: 0, cold: 300, gcPeriod: 0);
        h.Run(300);
        Assert.True(h.State.IsCapped);
        ulong cap = h.State.CapBytes;
        h.Sim.BaseFaultsPerTick = 20_000;   // 20k faults in 2 s at cap = 300 ms of estimated stall
        var a = h.Step();
        Assert.Equal(ActionKind.Raise, a.Kind);
        Assert.Equal(cap, a.PrevCapBytes);
        Assert.True(a.CapBytes >= Units.Scale(cap, h.Policy.IncreaseFactor) - Units.MiB);
        Assert.Equal(Units.Scale(cap, h.Policy.FloorMargin), h.State.FloorBytes);
    }

    [Fact]
    public void HardFaultsCountAsStall()
    {
        var h = Idle();
        h.Run(300);
        var x = h.Last with { HardFaultDelta = 100, FaultDelta = 0 };
        var ctx = new DecisionContext { NowMs = h.NowMs + 2000, NowUnix = h.NowUnix, Aggressiveness = 1, CapIntact = true, LastAppFocusMs = h.LastFocusMs };
        var a = Controller.Decide(h.State, x, h.Policy, ctx);
        Assert.Equal(ActionKind.Raise, a.Kind);
        Assert.Equal(RaiseReason.Stall, a.Raise);
    }

    [Fact]
    public void GraceFreezesCostDecisions()
    {
        var h = Idle();
        h.Run(300);
        ulong cap = h.State.CapBytes;
        h.Grace = true;
        h.Sim.BaseFaultsPerTick = 50_000;
        for (int i = 0; i < 30; i++) Assert.Equal(ActionKind.None, h.Step().Kind);
        Assert.Equal(cap, h.State.CapBytes);
    }

    [Fact]
    public void TamperedCapIsReapplied()
    {
        var h = Idle();
        h.Run(300);
        h.CapIntact = false;
        var a = h.Step();
        Assert.Equal(ActionKind.Reapply, a.Kind);
        Assert.Equal(h.State.CapBytes, a.CapBytes);
    }

    [Fact]
    public void YoungProcessesAreNotCapped()
    {
        // A trim seconds after start evicted hot pages in live tests: the kernel has no aging data yet.
        var h = Idle();
        h.ProcessAgeMs = -h.NowMs;   // process starts at harness time 0
        h.Run(h.Policy.MinProcessAgeSeconds - 10);
        Assert.Empty(h.Log);
        h.Run(30);
        Assert.Contains(h.Log, l => l.A.Kind == ActionKind.StartDescent);
    }

    [Fact]
    public void SmallProcessesAreLeftAlone()
    {
        var h = Idle(hot: 10, gc: 10, cold: 20);
        h.Run(1800);
        Assert.Empty(h.Log);
    }

    [Fact]
    public void FloorExpiresAfterTtl()
    {
        var p = new PolicyConfig();
        var s = new ControllerState { FloorBytes = Mb(100), FloorSetUnix = 1000 };
        Assert.Equal(Mb(100), Controller.EffectiveFloor(s, p, 1000 + 3600));
        Assert.Equal(0UL, Controller.EffectiveFloor(s, p, 1000 + (long)(p.FloorTtlHours * 3600) + 1));
    }

    [Fact]
    public void ApplyFailureBacksOff()
    {
        var h = Idle();
        h.Run(300);
        Controller.OnApplyFailed(h.State, h.NowMs);
        Assert.False(h.State.IsCapped);
        int n = h.Log.Count;
        h.Run(50);
        Assert.Equal(n, h.Log.Count);
        h.Run(30);
        Assert.True(h.Log.Count > n);
    }

    [Fact]
    public void LearnedStateMarksDirtyForPersistence()
    {
        var h = Idle();
        h.Run(600);
        Assert.True(h.State.LearnedDirty);
        Assert.True(h.State.LearnedCap > 0);
    }

    private static void BurstFor(Harness h, double seconds, int extraMb)
    {
        for (int i = 0; i < (int)(seconds / h.Policy.TickSeconds); i++)
        {
            h.Sim.Burst(extraMb, 900);
            h.Step();
        }
    }

    [Fact]
    public void StepFactorAndBudgetScaleWithAggressiveness()
    {
        var p = new PolicyConfig();
        Assert.Equal(p.StepFactor, Controller.EffectiveStepFactor(p, 1), 6);
        Assert.True(Controller.EffectiveStepFactor(p, 0) > p.StepFactor);
        Assert.True(Controller.Budget(p, 1) > Controller.Budget(p, 0));
        Assert.Equal(0UL, Controller.DepthLimit(Mb(100), p, 1));
        Assert.Equal(Mb(60), Controller.DepthLimit(Mb(100), p, 0.4));
    }
}
