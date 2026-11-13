namespace RamPilot.Core;

/// <summary>One process as seen by a system-wide snapshot (NtQuerySystemInformation on Windows).</summary>
/// <param name="CreateTime">FILETIME ticks; together with <paramref name="Pid"/> it identifies a process instance.</param>
/// <param name="PrivateBytes">Committed private bytes (commit charge), unaffected by working-set limits.</param>
public readonly record struct ProcEntry(
    uint Pid, uint ParentPid, string Exe, long CreateTime,
    ulong WorkingSet, ulong PrivateWorkingSet, ulong PrivateBytes,
    uint PageFaults, uint HardFaults, ulong CycleTime);

/// <summary>
/// Reusable process table. The engine refills it every tick (no per-tick allocation once warmed up).
/// <see cref="Valid"/> is false when the snapshot could not be taken or failed a bounds check; a caller must then
/// treat the tick as "no information" rather than "every process exited".
/// </summary>
public sealed class ProcessSnapshot
{
    /// <summary>Upper bound for ancestor walks; real trees are a handful of levels deep.</summary>
    public const int MaxAncestorDepth = 64;

    private readonly Dictionary<uint, ProcEntry> _byPid = new(512);

    public bool Valid { get; set; }

    public IReadOnlyDictionary<uint, ProcEntry> ByPid => _byPid;

    /// <summary>All entries; a concrete collection so foreach does not allocate an enumerator.</summary>
    public Dictionary<uint, ProcEntry>.ValueCollection Entries => _byPid.Values;

    public int Count => _byPid.Count;

    public void Clear()
    {
        _byPid.Clear();
        Valid = false;
    }

    public void Add(in ProcEntry e) => _byPid[e.Pid] = e;

    public bool TryGet(uint pid, out ProcEntry e) => _byPid.TryGetValue(pid, out e);

    /// <summary>
    /// Finds the application root of <paramref name="pid"/>: the top-most ancestor whose image is in
    /// <paramref name="targets"/>, walking up only through target or helper images. Returns null if the process is
    /// not part of an allowlisted tree.
    /// PID reuse: a parent PID can be recycled after the real parent exits. An ancestor created after its child
    /// cannot be the real parent, so the walk stops there.
    /// </summary>
    public ProcEntry? FindAppRoot(uint pid, IReadOnlySet<string> targets, IReadOnlySet<string> helpers)
    {
        ProcEntry? root = null;
        uint cur = pid;
        long childCreate = long.MaxValue;
        for (int depth = 0; depth < MaxAncestorDepth && _byPid.TryGetValue(cur, out var e); depth++)
        {
            if (e.CreateTime > childCreate) break;   // recycled PID: not the real parent
            if (targets.Contains(e.Exe)) root = e;
            else if (!helpers.Contains(e.Exe)) break;
            childCreate = e.CreateTime;
            if (e.ParentPid == 0 || e.ParentPid == cur) break;
            cur = e.ParentPid;
        }
        return root;
    }
}
