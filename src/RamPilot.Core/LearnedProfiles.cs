using System.Text.Json;

namespace RamPilot.Core;

/// <summary>What was learned for one (app, image, role): last good cap and the ssthresh-style floor.</summary>
public sealed class LearnedProfile
{
    public ulong CapBytes { get; set; }
    public ulong ReferenceWsBytes { get; set; }
    public ulong FloorBytes { get; set; }
    public long FloorSetUnix { get; set; }
    public long UpdatedUnix { get; set; }
}

public sealed class LearnedProfileFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, LearnedProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persisted per-(app, exe, role) knowledge, so a restarted app or engine does not start from scratch.
/// Several live processes can share a key (two renderers); the last writer wins. A profile is only a hint: the
/// controller still bounds every restore by the current working set and keeps measuring cost.
/// </summary>
public sealed class LearnedProfiles
{
    /// <summary>Profiles not updated for this long are dropped.</summary>
    public const long MaxAgeSeconds = 30L * 24 * 3600;
    public const int MaxEntries = 512;

    private readonly Dictionary<string, LearnedProfile> _map = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _map.Count;

    public static string Key(string appExe, string exe, string role) => $"{appExe}|{exe}|{role}";

    public bool TryGet(string key, out LearnedProfile profile) => _map.TryGetValue(key, out profile!);

    /// <summary>Seeds a fresh controller state from a stored profile.</summary>
    public void Seed(string key, ControllerState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!_map.TryGetValue(key, out var p)) return;
        s.LearnedCap = p.CapBytes;
        s.ReferenceWs = p.ReferenceWsBytes;
        s.FloorBytes = p.FloorBytes;
        s.FloorSetUnix = p.FloorSetUnix;
    }

    /// <summary>Stores what a controller state has learned. Returns true if anything changed.</summary>
    public bool Update(string key, ControllerState s, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.LearnedCap == 0 && s.FloorBytes == 0) return false;
        if (!_map.TryGetValue(key, out var p))
        {
            if (_map.Count >= MaxEntries) EvictOldest();
            p = new LearnedProfile();
            _map[key] = p;
        }
        bool changed = p.CapBytes != s.LearnedCap || p.FloorBytes != s.FloorBytes || p.ReferenceWsBytes != s.ReferenceWs;
        p.CapBytes = s.LearnedCap;
        p.ReferenceWsBytes = s.ReferenceWs;
        p.FloorBytes = s.FloorBytes;
        p.FloorSetUnix = s.FloorSetUnix;
        p.UpdatedUnix = nowUnix;
        return changed;
    }

    public string ToJson()
    {
        var f = new LearnedProfileFile();
        foreach (var kv in _map) f.Profiles[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(f, CoreJson.Default.LearnedProfileFile);
    }

    /// <summary>Loads profiles, dropping stale or implausible entries. Bad JSON yields an empty store.</summary>
    public static LearnedProfiles FromJson(string? json, long nowUnix)
    {
        var r = new LearnedProfiles();
        if (string.IsNullOrWhiteSpace(json)) return r;
        LearnedProfileFile? f;
        try { f = JsonSerializer.Deserialize(json, CoreJson.Default.LearnedProfileFile); }
        catch (JsonException) { return r; }
        if (f?.Profiles is null) return r;
        foreach (var kv in f.Profiles)
        {
            var p = kv.Value;
            if (p is null || kv.Key.Length > 400) continue;
            if (nowUnix - p.UpdatedUnix > MaxAgeSeconds || p.UpdatedUnix > nowUnix + 86_400) continue;
            if (p.CapBytes > (1UL << 40) || p.FloorBytes > (1UL << 40) || p.ReferenceWsBytes > (1UL << 40)) continue;
            if (r._map.Count >= MaxEntries) break;
            r._map[kv.Key] = p;
        }
        return r;
    }

    private void EvictOldest()
    {
        string? oldest = null;
        long t = long.MaxValue;
        foreach (var kv in _map)
            if (kv.Value.UpdatedUnix < t) { t = kv.Value.UpdatedUnix; oldest = kv.Key; }
        if (oldest is not null) _map.Remove(oldest);
    }
}
