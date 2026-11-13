using RamPilot.Core;

namespace RamPilot.Core.Tests;

/// <summary>
/// A toy model of an idle Electron/WebView2 process under a hard working-set maximum.
/// Memory is 1 MiB chunks: a hot set touched every tick, a "GC" region touched in a burst every GcPeriod seconds
/// (V8 major GC walks old-space), and cold memory that is never touched again. Eviction under the cap is LRU, which
/// is the assumption RamPilot makes about the kernel's working-set manager (aging by access bits).
/// A touch of a non-resident chunk costs 256 page faults (4 KiB pages).
/// </summary>
internal sealed class SimulatedProcess
{
    private const int PagesPerChunk = 256;
    private readonly bool[] _resident;
    private readonly long[] _lastTouch;
    private readonly int _hot, _gc;
    private long _t;
    private ulong _prevCycles, _cycles;
    private uint _faults, _prevFaults;
    private int _extraTouch;

    public SimulatedProcess(int hotMb, int gcMb, int coldMb, double gcPeriodSeconds = 120, int baseFaultsPerTick = 10)
    {
        _hot = hotMb;
        _gc = gcMb;
        TotalMb = hotMb + gcMb + coldMb;
        GcPeriodSeconds = gcPeriodSeconds;
        BaseFaultsPerTick = baseFaultsPerTick;
        _resident = new bool[TotalMb];
        _lastTouch = new long[TotalMb];
        for (int i = 0; i < TotalMb; i++) { _resident[i] = true; _lastTouch[i] = -1_000_000 - i; }
    }

    public int TotalMb { get; }
    public double GcPeriodSeconds { get; set; }
    public int BaseFaultsPerTick { get; set; }
    public ulong CapBytes { get; private set; }
    public int ResidentMb { get; private set; }
    public double TotalFaults { get; private set; }

    /// <summary>Next tick: burn CPU and touch <paramref name="extraMb"/> cold chunks (an "activity burst").</summary>
    public void Burst(int extraMb, double mcycles) { _extraTouch = extraMb; _burstCycles = mcycles; }
    private double _burstCycles;

    public void SetCap(ulong bytes) { CapBytes = bytes; Enforce(); }

    public void Uncap() => CapBytes = 0;

    public ProcessSample Tick(double dt)
    {
        _t += (long)(dt * 1000);
        for (int i = 0; i < _hot; i++) Touch(i);
        if (GcPeriodSeconds > 0 && (_t / 1000) % (long)GcPeriodSeconds < dt)
            for (int i = _hot; i < _hot + _gc; i++) Touch(i);
        for (int i = 0; i < _extraTouch && _hot + _gc + i < TotalMb; i++) Touch(_hot + _gc + i);
        _faults += (uint)BaseFaultsPerTick;
        _cycles += (ulong)((_burstCycles > 0 ? _burstCycles : 5) * 1e6 * dt);
        _extraTouch = 0;
        _burstCycles = 0;

        uint fd = _faults - _prevFaults;
        double cps = (_cycles - _prevCycles) / dt;
        _prevFaults = _faults;
        _prevCycles = _cycles;
        TotalFaults += fd;
        CountResident();
        ulong ws = (ulong)ResidentMb * Units.MiB;
        return new ProcessSample
        {
            Valid = true,
            DtSeconds = dt,
            WorkingSet = ws,
            PrivateWorkingSet = ws * 8 / 10,
            CommitBytes = (ulong)TotalMb * Units.MiB,
            FaultDelta = fd,
            HardFaultDelta = 0,
            CyclesPerSec = cps,
        };
    }

    private void Touch(int chunk)
    {
        _lastTouch[chunk] = _t;
        if (_resident[chunk]) return;
        _resident[chunk] = true;
        _faults += PagesPerChunk;
        Enforce();
    }

    private void Enforce()
    {
        if (CapBytes == 0) return;
        int capMb = (int)(CapBytes / Units.MiB);
        CountResident();
        while (ResidentMb > capMb)
        {
            int victim = -1;
            long oldest = long.MaxValue;
            for (int i = 0; i < TotalMb; i++)
                if (_resident[i] && _lastTouch[i] < oldest) { oldest = _lastTouch[i]; victim = i; }
            if (victim < 0) break;
            _resident[victim] = false;
            ResidentMb--;
        }
    }

    private void CountResident()
    {
        int n = 0;
        foreach (bool r in _resident) if (r) n++;
        ResidentMb = n;
    }
}

/// <summary>Drives <see cref="Controller"/> against a <see cref="SimulatedProcess"/> and records every action.</summary>
internal sealed class Harness
{
    public Harness(SimulatedProcess sim, PolicyConfig? policy = null, double aggressiveness = 1.0)
    {
        Sim = sim;
        Policy = policy ?? new PolicyConfig();
        Aggressiveness = aggressiveness;
    }

    public SimulatedProcess Sim { get; }
    public PolicyConfig Policy { get; }
    public ControllerState State { get; } = new();
    public double Aggressiveness { get; set; }
    public long NowMs { get; private set; } = 1_000_000;
    public long NowUnix { get; private set; } = 1_800_000_000;
    public long LastFocusMs { get; set; }
    public bool Focused { get; set; }
    public bool Audio { get; set; }
    public bool Paused { get; set; }
    public bool Grace { get; set; }
    public bool CapIntact { get; set; } = true;
    /// <summary>Age of the simulated process at harness time 0 (default: old enough to be managed).</summary>
    public long ProcessAgeMs { get; set; } = 3_600_000;
    public List<(long T, ControllerAction A, ulong WsBefore, ulong Floor)> Log { get; } = [];
    public ProcessSample Last { get; private set; }

    public ControllerAction Step()
    {
        double dt = Policy.TickSeconds;
        NowMs += Policy.TickMs;
        NowUnix += (long)dt;
        Last = Sim.Tick(dt);
        if (Focused) LastFocusMs = NowMs;
        var ctx = new DecisionContext
        {
            NowMs = NowMs,
            NowUnix = NowUnix,
            Paused = Paused,
            Focused = Focused,
            Audio = Audio,
            LastAppFocusMs = LastFocusMs,
            Aggressiveness = Aggressiveness,
            Grace = Grace,
            CapIntact = CapIntact,
            ProcessAgeMs = ProcessAgeMs + NowMs,
        };
        var a = Controller.Decide(State, Last, Policy, ctx);
        Apply(a);
        return a;
    }

    public void Run(double seconds)
    {
        int n = (int)(seconds / Policy.TickSeconds);
        for (int i = 0; i < n; i++) Step();
    }

    public void Apply(ControllerAction a)
    {
        if (a.Kind == ActionKind.None) return;
        Log.Add((NowMs, a, Last.WorkingSet, Controller.EffectiveFloor(State, Policy, NowUnix)));
        if (a.AppliesCap) Sim.SetCap(a.CapBytes);
        else if (a.Kind == ActionKind.Release) Sim.Uncap();
    }

    public int Count(ActionKind k) => Log.Count(l => l.A.Kind == k);
}
