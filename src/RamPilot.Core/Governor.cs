namespace RamPilot.Core;

public enum Pressure { Low, Medium, High }

/// <summary>System memory state, sampled once per tick.</summary>
/// <param name="CommitLimit">Commit limit in bytes (physical + page files).</param>
/// <param name="CommitAvailable">Commit headroom left in bytes.</param>
/// <param name="LowMemorySignaled">CreateMemoryResourceNotification(LowMemoryResourceNotification) is signaled.</param>
public readonly record struct MemorySample(
    ulong TotalPhys, ulong AvailPhys, ulong CommitLimit, ulong CommitAvailable, bool LowMemorySignaled)
{
    public double AvailPct => TotalPhys == 0 ? 100 : 100.0 * AvailPhys / TotalPhys;

    public double CommitPct => CommitLimit == 0 ? 0 : 100.0 * (CommitLimit - Math.Min(CommitAvailable, CommitLimit)) / CommitLimit;
}

/// <summary>
/// Demand-aware governor. RamPilot should not compress idle pages when the machine has plenty of free memory: that
/// costs CPU and buys nothing. The governor maps memory pressure to an aggressiveness in [0, 1] that the controller
/// uses to limit how deep caps go and how fast they descend.
///   High   : low-memory notification, or available &lt; HighAvailPct, or commit &gt; HighCommitPct
///   Medium : available &lt; MediumAvailPct or commit &gt; MediumCommitPct
///   Low    : otherwise
/// Escalation is immediate; de-escalation requires the lower level to persist for DeescalateSeconds (hysteresis).
/// </summary>
public sealed class Governor
{
    private long _lowerSinceMs = -1;

    public Pressure Level { get; private set; } = Pressure.Low;

    public double Aggressiveness { get; private set; }

    public Pressure Update(in MemorySample m, GovernorConfig g, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(g);
        Pressure raw = TryParsePressure(g.PressureOverride, out var forced) ? forced : Classify(m, g);
        if (raw >= Level || g.PressureOverride is not null)
        {
            Level = raw;
            _lowerSinceMs = -1;
        }
        else
        {
            if (_lowerSinceMs < 0) _lowerSinceMs = nowMs;
            if (nowMs - _lowerSinceMs >= (long)(g.DeescalateSeconds * 1000))
            {
                Level = raw;
                _lowerSinceMs = -1;
            }
        }
        Aggressiveness = Level switch
        {
            Pressure.High => g.HighAggressiveness,
            Pressure.Medium => g.MediumAggressiveness,
            _ => g.LowAggressiveness,
        };
        return Level;
    }

    public static Pressure Classify(in MemorySample m, GovernorConfig g)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (m.LowMemorySignaled || m.AvailPct < g.HighAvailPct || m.CommitPct > g.HighCommitPct) return Pressure.High;
        if (m.AvailPct < g.MediumAvailPct || m.CommitPct > g.MediumCommitPct) return Pressure.Medium;
        return Pressure.Low;
    }

    public static string Name(Pressure p) => p switch
    {
        Pressure.High => "high",
        Pressure.Medium => "medium",
        _ => "low",
    };

    public static bool TryParsePressure(string? s, out Pressure p)
    {
        switch (s?.Trim().ToUpperInvariant())
        {
            case "LOW": p = Pressure.Low; return true;
            case "MEDIUM": p = Pressure.Medium; return true;
            case "HIGH": p = Pressure.High; return true;
            default: p = Pressure.Low; return false;
        }
    }
}
