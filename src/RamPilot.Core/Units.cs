namespace RamPilot.Core;

/// <summary>Byte/megabyte helpers. "MB" in this project always means MiB (2^20 bytes).</summary>
public static class Units
{
    public const ulong MiB = 1UL << 20;

    public static double ToMb(ulong bytes) => bytes / (double)MiB;

    public static ulong FromMb(double mb) => mb <= 0 || double.IsNaN(mb) ? 0 : (ulong)(mb * MiB);

    public static double Round1(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    /// <summary>Multiplies a byte count by a non-negative factor without overflow surprises.</summary>
    public static ulong Scale(ulong bytes, double factor) =>
        factor <= 0 || double.IsNaN(factor) ? 0 : (ulong)Math.Min(bytes * factor, ulong.MaxValue / 2.0);
}
