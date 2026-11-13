namespace RamPilot.Core;

/// <summary>
/// Honest savings accounting (see docs/DESIGN.md, "Savings estimation").
///
/// reclaimed (per process) = U − P, where P is the current private working set and U is the counterfactual private
/// working set without a cap. U is estimated from the process's own uncapped trajectory: the private WS at entry,
/// scaled down by the change in its commit charge (which a working-set cap does not affect):
///     U = P_entry × min(1, C_now / C_entry)
/// If the app frees memory itself (commit falls), U falls with it, so the app's own shrinking is never credited.
/// If the app grows, U is not scaled up (conservative).
///
/// storeCost = reclaimed × r, where r is the measured ratio of Memory Compression store growth to private bytes
/// trimmed, observed around RamPilot's own trims (EWMA, clamped, with a prior).
/// net = reclaimed − storeCost.
/// </summary>
public static class Savings
{
    public static ulong Counterfactual(ulong entryPrivate, ulong entryCommit, ulong commitNow)
    {
        if (entryCommit == 0 || commitNow >= entryCommit) return entryPrivate;
        return Units.Scale(entryPrivate, (double)commitNow / entryCommit);
    }

    public static ulong Reclaimed(ulong entryPrivate, ulong entryCommit, ulong privateNow, ulong commitNow)
    {
        ulong u = Counterfactual(entryPrivate, entryCommit, commitNow);
        return u > privateNow ? u - privateNow : 0;
    }

    public static SavingsSummary Summarize(ulong reclaimedBytes, double storeRatio)
    {
        double reclaimed = Units.ToMb(reclaimedBytes);
        double cost = reclaimed * Math.Clamp(storeRatio, 0, 1);
        return new SavingsSummary(Units.Round1(reclaimed), Units.Round1(cost), Units.Round1(Math.Max(0, reclaimed - cost)));
    }
}

public readonly record struct SavingsSummary(double ReclaimedMb, double StoreCostMb, double NetMb);

/// <summary>Per-process entry baseline for <see cref="Savings"/>.</summary>
public sealed class SavingsTracker
{
    public bool Active { get; private set; }
    public ulong EntryPrivate { get; private set; }
    public ulong EntryCommit { get; private set; }

    public void Begin(ulong privateWs, ulong commit)
    {
        Active = true;
        EntryPrivate = privateWs;
        EntryCommit = commit;
    }

    public void End() => Active = false;

    public ulong Reclaimed(ulong privateWs, ulong commit) =>
        Active ? Savings.Reclaimed(EntryPrivate, EntryCommit, privateWs, commit) : 0;
}

/// <summary>
/// Estimates how many bytes of compression store one byte of trimmed private memory costs.
/// Each observation: private bytes RamPilot trimmed in a short window, and the store growth over the same window.
/// Other processes also feed the store, which biases r upward, i.e. towards a lower (more conservative) net.
/// </summary>
public sealed class StoreRatioEstimator
{
    public const double Prior = 0.35;
    public const double PriorWeightBytes = 32.0 * Units.MiB;
    /// <summary>
    /// Lower bound. Trimmed dirty pages may sit on the modified list (still resident, uncompressed) before the store
    /// absorbs them, so a short observation can show almost no store growth; that must not read as "free".
    /// Phase-0 measurement: +38 MB store for 154 MB of WS trimmed (about 0.25 of WS, more of private).
    /// </summary>
    public const double MinRatio = 0.25;
    public const double MaxRatio = 1.0;
    public const ulong MinObservationBytes = 4 * Units.MiB;
    /// <summary>Weight kept per observation, so the estimate follows changes in content compressibility.</summary>
    public const double Decay = 0.9;

    private double _trimmed, _stored;

    public int Observations { get; private set; }

    public double Ratio => Math.Clamp((Prior * PriorWeightBytes + _stored) / (PriorWeightBytes + _trimmed), MinRatio, MaxRatio);

    /// <returns>True if the observation was used.</returns>
    public bool Observe(ulong trimmedPrivateBytes, long storeDeltaBytes)
    {
        if (trimmedPrivateBytes < MinObservationBytes) return false;
        double r = Math.Clamp(storeDeltaBytes / (double)trimmedPrivateBytes, 0, 1.5);
        _trimmed = _trimmed * Decay + trimmedPrivateBytes;
        _stored = _stored * Decay + r * trimmedPrivateBytes;
        Observations++;
        return true;
    }
}
