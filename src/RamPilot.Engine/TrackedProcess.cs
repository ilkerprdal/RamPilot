using RamPilot.Core;
using RamPilot.Win32;

namespace RamPilot;

/// <summary>One process of a managed app tree: open handle + measurements + controller state.</summary>
internal sealed class TrackedProcess : IDisposable
{
    public TrackedProcess(ProcessHandle handle, in ProcEntry e, string appExe, uint rootPid, ProcessClass cls)
    {
        Handle = handle;
        Pid = e.Pid;
        Exe = e.Exe;
        CreateTime = e.CreateTime;
        AppExe = appExe;
        RootPid = rootPid;
        Role = cls.Role;
        Excluded = cls.Excluded;
        ExcludeReason = cls.Reason;
        ProfileKey = LearnedProfiles.Key(appExe, e.Exe, cls.Role);
        Last = e;
    }

    public ProcessHandle Handle { get; }
    public uint Pid { get; }
    public string Exe { get; }
    public long CreateTime { get; }
    public string Role { get; }
    public bool Excluded { get; }
    public string? ExcludeReason { get; }
    public string ProfileKey { get; }

    /// <summary>Root image of the app tree; refreshed every tick (roots can be replaced).</summary>
    public string AppExe { get; set; }
    public uint RootPid { get; set; }

    public ControllerState State { get; } = new();
    public SavingsTracker Savings { get; } = new();

    public ProcEntry Last { get; private set; }
    public uint FaultDelta { get; private set; }
    public uint HardDelta { get; private set; }
    public double CyclesPerSec { get; private set; }
    public bool DeltaValid { get; private set; }
    public double DtSeconds { get; private set; }
    public int SeenGen { get; set; }
    /// <summary>A release failed: the journal entry is kept and the release is retried every tick.</summary>
    public bool ReleasePending { get; set; }

    public void Update(in ProcEntry e, double dtSec, bool valid)
    {
        DeltaValid = valid && dtSec > 0 && e.PageFaults >= Last.PageFaults && e.CycleTime >= Last.CycleTime;
        FaultDelta = e.PageFaults >= Last.PageFaults ? e.PageFaults - Last.PageFaults : 0;
        HardDelta = e.HardFaults >= Last.HardFaults ? e.HardFaults - Last.HardFaults : 0;
        CyclesPerSec = dtSec > 0 && e.CycleTime >= Last.CycleTime ? (e.CycleTime - Last.CycleTime) / dtSec : 0;
        DtSeconds = dtSec;
        Last = e;
    }

    public ProcessSample Sample() => new()
    {
        Valid = DeltaValid,
        DtSeconds = DtSeconds,
        WorkingSet = Last.WorkingSet,
        PrivateWorkingSet = Last.PrivateWorkingSet,
        CommitBytes = Last.PrivateBytes,
        FaultDelta = FaultDelta,
        HardFaultDelta = HardDelta,
        CyclesPerSec = CyclesPerSec,
    };

    public ulong Reclaimed => Savings.Reclaimed(Last.PrivateWorkingSet, Last.PrivateBytes);

    public string Describe() => $"{AppExe}/{Exe}({Pid}) {Role}";

    public void Dispose() => Handle.Dispose();
}
