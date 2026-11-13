using System.Runtime.InteropServices;
using RamPilot.Core;

namespace RamPilot.Win32;

/// <summary>
/// System-wide process snapshot via NtQuerySystemInformation(SystemProcessInformation): working set, private WS,
/// commit, page faults, hard faults, cycle time and parent for every process in one call, no admin rights needed.
///
/// The SYSTEM_PROCESS_INFORMATION layout is not a documented contract, so:
///  - every entry is bounds-checked against the returned length (a violation invalidates the snapshot), and
///  - <see cref="SelfCheck"/> compares our own entry with documented APIs at startup; the engine refuses to run on a
///    mismatch instead of steering by garbage.
/// Allocation: the buffer, the snapshot dictionary and image-name strings are reused across ticks.
/// </summary>
internal sealed unsafe class ProcessSampler
{
    // x64 offsets
    private const int O_Next = 0, O_Threads = 4, O_WsPrivate = 8, O_HardFaults = 16, O_Cycle = 24, O_Create = 32,
        O_NameLen = 56, O_NameBuf = 64, O_Pid = 80, O_Parent = 88, O_Session = 100, O_PageFaults = 128,
        O_Ws = 144, O_PagefileUsage = 184, O_PrivatePages = 200;
    private const int EntrySize = 256;
    private const int ThreadInfoSize = 80;
    private const int SystemProcessInformation = 5;
    private const int MaxBufferBytes = 64 << 20;

    private byte[] _buf = new byte[1 << 20];
    private readonly Dictionary<(uint Pid, long Create), (string Name, int Gen)> _names = new(512);
    private readonly List<(uint, long)> _stale = [];
    private int _gen;

    public uint SelfPid { get; } = (uint)Environment.ProcessId;
    public uint SelfSessionFromTable { get; private set; }
    public ulong SelfPagefileUsage { get; private set; }
    public string? LastError { get; private set; }

    public ProcessSampler()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("RamPilot requires a 64-bit process.");
    }

    public bool Capture(ProcessSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        snap.Clear();
        _gen++;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int st;
            uint ret;
            fixed (byte* p = _buf)
            {
                st = Native.NtQuerySystemInformation(SystemProcessInformation, p, (uint)_buf.Length, out ret);
                if (st >= 0)
                {
                    bool ok = Parse(p, ret == 0 || ret > (uint)_buf.Length ? _buf.Length : (int)ret, snap);
                    snap.Valid = ok;
                    if (!ok) snap.Clear();
                    if (ok && (_gen & 63) == 0) PruneNames();
                    return ok;
                }
            }
            if (st != Native.STATUS_INFO_LENGTH_MISMATCH) { LastError = $"NtQuerySystemInformation 0x{st:X8}"; return false; }
            long want = Math.Max((long)ret + (1 << 16), (long)_buf.Length * 2);
            if (want > MaxBufferBytes) { LastError = "process table larger than 64 MB"; return false; }
            _buf = new byte[want];
        }
        LastError = "process table kept growing";
        return false;
    }

    private bool Parse(byte* p, int len, ProcessSnapshot snap)
    {
        int off = 0;
        for (int count = 0; count < 100_000; count++)
        {
            if (off < 0 || off + EntrySize > len) return Fail("entry beyond buffer");
            byte* e = p + off;
            uint next = *(uint*)(e + O_Next);
            uint threads = *(uint*)(e + O_Threads);
            if (next != 0 && (next < EntrySize || off + (long)next > len)) return Fail("bad NextEntryOffset");
            if (next != 0 && next < EntrySize + (long)threads * ThreadInfoSize) return Fail("entry smaller than its thread array");

            uint pid = (uint)*(nint*)(e + O_Pid);
            long create = *(long*)(e + O_Create);
            ushort nameLen = *(ushort*)(e + O_NameLen);
            byte* nameBuf = *(byte**)(e + O_NameBuf);
            string name;
            if (pid == 0) name = "Idle";
            else if (nameBuf == null || nameLen == 0) name = "";
            else
            {
                if ((nameLen & 1) != 0 || nameLen > 1024 || nameBuf < p || nameBuf + nameLen > p + len)
                    return Fail("image name outside buffer");
                name = Intern(pid, create, (char*)nameBuf, nameLen / 2);
            }

            ulong ws = (ulong)*(nuint*)(e + O_Ws);
            ulong pws = (ulong)*(long*)(e + O_WsPrivate);
            uint pf = *(uint*)(e + O_PageFaults);
            uint hf = *(uint*)(e + O_HardFaults);
            snap.Add(new ProcEntry(pid, (uint)*(nint*)(e + O_Parent), name, create,
                ws, pws, (ulong)*(nuint*)(e + O_PrivatePages), pf, hf, *(ulong*)(e + O_Cycle)));
            if (pid == SelfPid)
            {
                SelfSessionFromTable = *(uint*)(e + O_Session);
                SelfPagefileUsage = (ulong)*(nuint*)(e + O_PagefileUsage);
            }
            if (next == 0) return true;
            off += (int)next;
        }
        return Fail("too many entries");
    }

    private bool Fail(string why)
    {
        LastError = "process table bounds check failed: " + why;
        return false;
    }

    private string Intern(uint pid, long create, char* chars, int length)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_names, (pid, create), out bool exists);
        if (!exists || slot.Name is null || slot.Name.Length != length || !new ReadOnlySpan<char>(chars, length).SequenceEqual(slot.Name))
            slot.Name = new string(chars, 0, length);
        slot.Gen = _gen;
        return slot.Name;
    }

    private void PruneNames()
    {
        _stale.Clear();
        foreach (var kv in _names)
            if (_gen - kv.Value.Gen > 2) _stale.Add(kv.Key);
        foreach (var k in _stale) _names.Remove(k);
    }

    /// <summary>
    /// Startup self-check: our own snapshot entry must agree with documented APIs. Returns null if consistent,
    /// otherwise a description of the mismatch.
    /// </summary>
    public string? SelfCheck()
    {
        var snap = new ProcessSnapshot();
        if (!Capture(snap)) return LastError ?? "snapshot failed";
        nint me = Native.GetCurrentProcess();
        var pmc = new Native.PROCESS_MEMORY_COUNTERS_EX { cb = (uint)sizeof(Native.PROCESS_MEMORY_COUNTERS_EX) };
        if (Native.GetProcessMemoryInfo(me, out pmc, pmc.cb) == 0) return "GetProcessMemoryInfo failed";
        if (Native.GetProcessTimes(me, out long created, out _, out _, out _) == 0) return "GetProcessTimes failed";
        if (Native.ProcessIdToSessionId(SelfPid, out uint session) == 0) return "ProcessIdToSessionId failed";
        _ = Native.QueryProcessCycleTime(me, out ulong cycles);
        if (!snap.TryGet(SelfPid, out var e)) return "own process missing from the table";

        string exe = Path.GetFileName(Environment.ProcessPath ?? "");
        var problems = new List<string>();
        if (e.CreateTime != created) problems.Add($"create time {e.CreateTime} != {created}");
        if (SelfSessionFromTable != session) problems.Add($"session {SelfSessionFromTable} != {session}");
        if (exe.Length > 0 && !e.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) problems.Add($"name '{e.Exe}' != '{exe}'");
        if (!Near(e.WorkingSet, pmc.WorkingSetSize)) problems.Add($"working set {e.WorkingSet} vs {pmc.WorkingSetSize}");
        if (!Near(e.PrivateBytes, pmc.PrivateUsage)) problems.Add($"private bytes {e.PrivateBytes} vs {pmc.PrivateUsage}");
        if (!Near(SelfPagefileUsage, pmc.PagefileUsage)) problems.Add($"pagefile usage {SelfPagefileUsage} vs {pmc.PagefileUsage}");
        if (e.PageFaults > pmc.PageFaultCount || pmc.PageFaultCount - e.PageFaults > 100_000) problems.Add($"page faults {e.PageFaults} vs {pmc.PageFaultCount}");
        if (e.PrivateWorkingSet > e.WorkingSet) problems.Add("private WS larger than WS");
        if (e.HardFaults > e.PageFaults) problems.Add("hard faults exceed page faults");
        if (cycles != 0 && (e.CycleTime == 0 || e.CycleTime > cycles)) problems.Add($"cycle time {e.CycleTime} vs {cycles}");
        if (e.ParentPid == SelfPid) problems.Add("parent pid equals own pid");
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static bool Near(ulong a, ulong b)
    {
        ulong d = a > b ? a - b : b - a;
        return d <= Math.Max(a, b) / 4 + (8UL << 20);
    }
}
