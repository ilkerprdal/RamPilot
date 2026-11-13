using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RamPilot.Win32;

/// <summary>
/// Which processes have an ACTIVE audio session (playing or capturing) right now, via the Core Audio session API
/// (IMMDeviceEnumerator → IAudioSessionManager2 → IAudioSessionControl2). Render and capture endpoints are both
/// enumerated, so a voice call protects the app even when nobody is speaking. Source-generated COM interop
/// ([GeneratedComInterface] + StrategyBasedComWrappers), which is Native AOT compatible.
/// Must be called from an MTA thread.
/// </summary>
internal sealed class AudioSessions
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const int EDataFlowAll = 2;
    private const uint DeviceStateActive = 1;
    private const int AudioSessionStateActive = 1;

    private readonly StrategyBasedComWrappers _cw = new();
    private readonly Action<string> _log;
    private IMMDeviceEnumerator? _enumerator;
    private int _failures;
    private bool _loggedFailure;

    public AudioSessions(Action<string> log) => _log = log;

    /// <summary>True once a query has succeeded; false means audio protection is unavailable.</summary>
    public bool Available { get; private set; }

    /// <summary>Fills <paramref name="pids"/> with processes that own an active session. False on failure.</summary>
    public bool Poll(HashSet<uint> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        pids.Clear();
        _owned.Clear();
        try
        {
            _enumerator ??= Create();
            if (_enumerator is null) return Fail("cannot create MMDeviceEnumerator");
            if (_enumerator.EnumAudioEndpoints(EDataFlowAll, DeviceStateActive, out nint pDevices) < 0 || pDevices == 0)
                return Fail("EnumAudioEndpoints failed");
            var devices = Own<IMMDeviceCollection>(pDevices);
            if (devices.GetCount(out uint n) < 0) return Fail("IMMDeviceCollection.GetCount failed");
            for (uint i = 0; i < n; i++)
            {
                if (devices.Item(i, out nint pDevice) < 0 || pDevice == 0) continue;
                var device = Own<IMMDevice>(pDevice);
                if (device.Activate(IidAudioSessionManager2, Native.CLSCTX_ALL, 0, out nint pMgr) < 0 || pMgr == 0) continue;
                var mgr = Own<IAudioSessionManager2>(pMgr);
                if (mgr.GetSessionEnumerator(out nint pSessions) < 0 || pSessions == 0) continue;
                var sessions = Own<IAudioSessionEnumerator>(pSessions);
                if (sessions.GetCount(out int count) < 0) continue;
                for (int j = 0; j < count; j++)
                {
                    if (sessions.GetSession(j, out nint pCtl) < 0 || pCtl == 0) continue;
                    var ctl = Own<IAudioSessionControl>(pCtl);
                    if (ctl.GetState(out int state) < 0 || state != AudioSessionStateActive) continue;
                    if (ctl is IAudioSessionControl2 ctl2 && ctl2.GetProcessId(out uint pid) >= 0 && pid != 0) pids.Add(pid);
                }
            }
            Available = true;
            _failures = 0;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            _enumerator = null;
            return Fail(ex.Message);
        }
        finally
        {
            // Release every COM object of this poll now, instead of waiting for a GC that this engine rarely runs.
            foreach (var o in _owned) o.FinalRelease();
            _owned.Clear();
        }
    }

    private readonly List<ComObject> _owned = [];

    /// <summary>Wraps a returned interface pointer in a unique RCW that <see cref="Poll"/> releases explicitly.</summary>
    private T Own<T>(nint p) where T : class
    {
        try
        {
            object o = _cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
            if (o is ComObject c) _owned.Add(c);
            return (T)o;
        }
        finally
        {
            Marshal.Release(p);   // the RCW holds its own reference
        }
    }

    private bool Fail(string why)
    {
        if (++_failures >= 3) _enumerator = null;
        if (!_loggedFailure)
        {
            _loggedFailure = true;
            _log("warning: audio session query failed (" + why + "); audio protection degraded to the CPU-activity heuristic");
        }
        return false;
    }

    private IMMDeviceEnumerator? Create()
    {
        int hr = Native.CoCreateInstance(ClsidMMDeviceEnumerator, 0, 1 /* CLSCTX_INPROC_SERVER */,
            typeof(IMMDeviceEnumerator).GUID, out nint p);
        if (hr < 0 || p == 0) return null;
        try { return (IMMDeviceEnumerator)_cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None); }
        finally { Marshal.Release(p); }
    }
}

// Vtable order matters; trailing methods we never call are omitted.

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out nint device);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint manager);
}

[GeneratedComInterface]
[Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4")]
internal partial interface IAudioSessionManager
{
    [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint streamFlags, out nint control);
    [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint streamFlags, out nint volume);
}

[GeneratedComInterface]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2 : IAudioSessionManager
{
    [PreserveSig] int GetSessionEnumerator(out nint sessions);
}

[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out nint session);
}

[GeneratedComInterface]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
internal partial interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out nint name);
    [PreserveSig] int SetDisplayName(nint name, nint eventContext);
    [PreserveSig] int GetIconPath(out nint path);
    [PreserveSig] int SetIconPath(nint path, nint eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(nint groupingParam, nint eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(nint client);
    [PreserveSig] int UnregisterAudioSessionNotification(nint client);
}

[GeneratedComInterface]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl2 : IAudioSessionControl
{
    [PreserveSig] int GetSessionIdentifier(out nint id);
    [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
    [PreserveSig] int GetProcessId(out uint pid);
}
