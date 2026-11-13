using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamPilot.Core;

/// <summary>An allowlisted application: the root image whose process tree may be managed.</summary>
public sealed class AppEntry
{
    public string Exe { get; set; } = "";
    public string? Name { get; set; }
}

/// <summary>
/// Contents of %LOCALAPPDATA%\RamPilot\config.json. Every numeric value is clamped to a safe range by
/// <see cref="Sanitize"/>; the built-in <see cref="DenyList"/> always wins over the user's allowlist.
/// </summary>
public sealed class EngineConfig
{
    public int Version { get; set; } = 1;

    /// <summary>Allowlist. Only these trees are ever touched.</summary>
    public List<AppEntry> Apps { get; set; } =
    [
        new() { Exe = "WhatsApp.Root.exe", Name = "WhatsApp" },
        new() { Exe = "WhatsApp.exe", Name = "WhatsApp" },
        new() { Exe = "Discord.exe", Name = "Discord" },
        new() { Exe = "ChatGPT.exe", Name = "ChatGPT" },
        new() { Exe = "Slack.exe", Name = "Slack" },
        new() { Exe = "ms-teams.exe", Name = "Teams" },
        new() { Exe = "Teams.exe", Name = "Teams (classic)" },
        new() { Exe = "Notion.exe", Name = "Notion" },
        new() { Exe = "Spotify.exe", Name = "Spotify" },
    ];

    /// <summary>Helper images that belong to the tree of an allowlisted root (WebView2 children).</summary>
    public List<string> Helpers { get; set; } = ["msedgewebview2.exe"];

    /// <summary>Extra command-line markers that exclude a process (in addition to the built-in ones).</summary>
    public List<string> ExcludeCommandLine { get; set; } = [];

    public PolicyConfig Policy { get; set; } = new();

    public GovernorConfig Governor { get; set; } = new();

    /// <summary>
    /// Returns a sanitized copy: values clamped, deny-listed and malformed entries removed, duplicates dropped.
    /// Every adjustment is reported in <paramref name="warnings"/>.
    /// </summary>
    public EngineConfig Sanitize(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        var c = new EngineConfig { Version = 1, Apps = [], Helpers = [], ExcludeCommandLine = [] };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Apps ?? [])
        {
            string exe = (a?.Exe ?? "").Trim();
            if (!IsImageName(exe)) { warnings.Add($"apps: ignored invalid image name '{exe}'"); continue; }
            if (DenyList.IsDeniedExe(exe)) { warnings.Add($"apps: '{exe}' is on the built-in deny list and cannot be managed"); continue; }
            if (!seen.Add(exe)) continue;
            string? name = string.IsNullOrWhiteSpace(a!.Name) ? null : a.Name.Trim();
            if (name is { Length: > 64 }) name = name[..64];
            c.Apps.Add(new AppEntry { Exe = exe, Name = name });
        }
        foreach (var h in Helpers ?? [])
        {
            string exe = (h ?? "").Trim();
            if (!IsImageName(exe)) { warnings.Add($"helpers: ignored invalid image name '{exe}'"); continue; }
            if (DenyList.IsDeniedExe(exe)) { warnings.Add($"helpers: '{exe}' is on the built-in deny list"); continue; }
            if (seen.Add(exe)) c.Helpers.Add(exe);
        }
        foreach (var x in ExcludeCommandLine ?? [])
            if (!string.IsNullOrWhiteSpace(x) && x.Length <= 256) c.ExcludeCommandLine.Add(x.Trim());
        c.Policy = (Policy ?? new PolicyConfig()).Sanitize(warnings);
        c.Governor = (Governor ?? new GovernorConfig()).Sanitize(warnings);
        return c;
    }

    private static readonly System.Buffers.SearchValues<char> BadNameChars = System.Buffers.SearchValues.Create("\\/:*?\"<>|");

    private static bool IsImageName(string exe) =>
        exe.Length is > 4 and <= 128 && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && exe.AsSpan().IndexOfAny(BadNameChars) < 0;

    public string DisplayName(string exe)
    {
        foreach (var a in Apps)
            if (a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) && a.Name is not null) return a.Name;
        return Path.GetFileNameWithoutExtension(exe);
    }

    public string ToJson() => JsonSerializer.Serialize(this, CoreJson.Default.EngineConfig);

    /// <summary>Parses config JSON (comments and trailing commas allowed). Throws <see cref="JsonException"/> on bad input.</summary>
    public static EngineConfig FromJson(string json) =>
        JsonSerializer.Deserialize(json, CoreJson.Default.EngineConfig) ?? new EngineConfig();

    internal static double Clamp(string name, double value, double min, double max, double fallback, List<string> warnings)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            warnings.Add($"{name}: invalid value, using {F(fallback)}");
            return fallback;
        }
        if (value < min || value > max)
        {
            double v = Math.Clamp(value, min, max);
            warnings.Add($"{name}: {F(value)} out of range [{F(min)}, {F(max)}], using {F(v)}");
            return v;
        }
        return value;
    }

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Controller tunables. See docs/DESIGN.md for the meaning of each value.</summary>
public sealed class PolicyConfig
{
    /// <summary>Fixed sampling cadence.</summary>
    public double TickSeconds { get; set; } = 2;
    /// <summary>
    /// Before the FIRST cap of a process it must have been idle (unfocused, no sustained CPU load, no audio) this long.
    /// Short CPU bursts do not reset this clock. Measured (experiments/README.md, probe A): after 30/60/120 s the first
    /// trim under a new hard max evicted hot and cold pages alike (insertion order); after 300 s it kept the hot set in
    /// 5 of 6 runs (73.5 % in one).
    /// </summary>
    public double FirstCapIdleSeconds { get; set; } = 300;
    /// <summary>After an app loses focus, wait this long before capping it again (re-entry).</summary>
    public double FocusGraceSeconds { get; set; } = 60;
    /// <summary>A sample above this CPU rate (million cycles per second; 150 ≈ 4 % of one core) counts as busy.</summary>
    public double ActiveMcyclesPerSec { get; set; } = 150;
    /// <summary>
    /// Sustained activity = busy share of recent time ≥ this fraction (EWMA, time constant ActiveWindowSeconds).
    /// 0.5 with a 60 s window ignores the 10–30 s notification bursts measured on idle WhatsApp and reacts to
    /// continuous load after about 42 s. Calls and video are caught instantly by audio detection.
    /// </summary>
    public double SustainedActiveFraction { get; set; } = 0.5;
    public double ActiveWindowSeconds { get; set; } = 60;
    public double ActiveHoldSeconds { get; set; } = 30;
    /// <summary>Keep an app uncapped this long after its last active audio session.</summary>
    public double AudioHoldSeconds { get; set; } = 30;
    /// <summary>
    /// A process younger than this is not capped. Live measurement: a trim seconds after start evicted hot pages
    /// (the kernel has no aging information yet), the same trim ten minutes later caused no refaults.
    /// </summary>
    public double MinProcessAgeSeconds { get; set; } = 300;
    /// <summary>Processes with a smaller working set are left alone.</summary>
    public double MinWsToManageMb { get; set; } = 48;
    /// <summary>Absolute lowest cap.</summary>
    public double MinCapMb { get; set; } = 32;
    /// <summary>
    /// First cap = working set x StartRatio. Gentle on purpose: the first application of a cap is the one step that
    /// may still evict by insertion order; later 15 % steps kept 94-100 % of the hot set (probe A3).
    /// Also bounds the first step of a learned-cap restore.
    /// </summary>
    public double StartRatio { get; set; } = 0.9;
    /// <summary>Multiplicative step per descent window (at full aggressiveness).</summary>
    public double StepFactor { get; set; } = 0.85;
    public double StepSeconds { get; set; } = 30;
    /// <summary>Step interval while walking back down to an already-learned cap.</summary>
    public double FastStepSeconds { get; set; } = 10;
    public double IncreaseFactor { get; set; } = 1.5;
    /// <summary>After a raise at cap C the floor becomes C x FloorMargin (ssthresh).</summary>
    public double FloorMargin { get; set; } = 1.1;
    public double FloorTtlHours { get; set; } = 24;
    /// <summary>Faults only count as refaults while WS is at least cap x AtCapRatio.</summary>
    public double AtCapRatio { get; set; } = 0.95;
    /// <summary>Estimated stall budget as a fraction of wall time (0.001 = 1 ms per second).</summary>
    public double StallBudget { get; set; } = 0.001;
    /// <summary>Descend only if the window cost is below budget x DescendHeadroom.</summary>
    public double DescendHeadroom { get; set; } = 0.5;
    /// <summary>A single tick above budget x SpikeMultiple raises the cap immediately.</summary>
    public double SpikeMultiple { get; set; } = 10;
    /// <summary>
    /// Budgeted cost of one refault, microseconds. Measured CPU cost per page (probe B): ~0.8 us from standby,
    /// ~1.65 us from the compression store, ~7 us averaged from NVMe. The default is deliberately higher: it stands
    /// for user-visible stall (bursts land on the UI thread when the app wakes, cache/TLB refill, slower disks), and
    /// a budget that is too loose costs latency while one that is too tight only costs savings.
    /// </summary>
    public double RefaultCostUs { get; set; } = 30;
    /// <summary>Estimated cost of one hard fault (disk read), microseconds.</summary>
    public double HardFaultCostUs { get; set; } = 2000;
    /// <summary>A holding cap is re-tested (one more step down) after this many clean minutes.</summary>
    public double HoldRetestMinutes { get; set; } = 15;
    /// <summary>Depth limit = reference WS x (1 - aggressiveness x MaxDepth).</summary>
    public double MaxDepth { get; set; } = 1.0;

    [JsonIgnore] public long TickMs => (long)(TickSeconds * 1000);
    [JsonIgnore] public long FocusGraceMs => (long)(FocusGraceSeconds * 1000);
    [JsonIgnore] public long FirstCapIdleMs => (long)(FirstCapIdleSeconds * 1000);
    [JsonIgnore] public long ActiveHoldMs => (long)(ActiveHoldSeconds * 1000);
    [JsonIgnore] public long AudioHoldMs => (long)(AudioHoldSeconds * 1000);
    [JsonIgnore] public double ActiveCyclesPerSec => ActiveMcyclesPerSec * 1e6;
    [JsonIgnore] public long MinProcessAgeMs => (long)(MinProcessAgeSeconds * 1000);
    [JsonIgnore] public ulong MinWsToManage => Units.FromMb(MinWsToManageMb);
    [JsonIgnore] public ulong MinCap => Units.FromMb(MinCapMb);
    [JsonIgnore] public long HoldRetestMs => (long)(HoldRetestMinutes * 60_000);

    public PolicyConfig Sanitize(List<string> w) => new()
    {
        TickSeconds = EngineConfig.Clamp("policy.tickSeconds", TickSeconds, 1, 10, 2, w),
        FirstCapIdleSeconds = EngineConfig.Clamp("policy.firstCapIdleSeconds", FirstCapIdleSeconds, 120, 3600, 300, w),
        FocusGraceSeconds = EngineConfig.Clamp("policy.focusGraceSeconds", FocusGraceSeconds, 10, 3600, 60, w),
        ActiveMcyclesPerSec = EngineConfig.Clamp("policy.activeMcyclesPerSec", ActiveMcyclesPerSec, 20, 5000, 150, w),
        SustainedActiveFraction = EngineConfig.Clamp("policy.sustainedActiveFraction", SustainedActiveFraction, 0.2, 1.0, 0.5, w),
        ActiveWindowSeconds = EngineConfig.Clamp("policy.activeWindowSeconds", ActiveWindowSeconds, 10, 600, 60, w),
        ActiveHoldSeconds = EngineConfig.Clamp("policy.activeHoldSeconds", ActiveHoldSeconds, 5, 3600, 30, w),
        AudioHoldSeconds = EngineConfig.Clamp("policy.audioHoldSeconds", AudioHoldSeconds, 5, 3600, 30, w),
        MinProcessAgeSeconds = EngineConfig.Clamp("policy.minProcessAgeSeconds", MinProcessAgeSeconds, 0, 3600, 300, w),
        MinWsToManageMb = EngineConfig.Clamp("policy.minWsToManageMb", MinWsToManageMb, 16, 8192, 48, w),
        MinCapMb = EngineConfig.Clamp("policy.minCapMb", MinCapMb, 16, 4096, 32, w),
        StartRatio = EngineConfig.Clamp("policy.startRatio", StartRatio, 0.8, 0.95, 0.9, w),
        StepFactor = EngineConfig.Clamp("policy.stepFactor", StepFactor, 0.6, 0.95, 0.85, w),
        StepSeconds = EngineConfig.Clamp("policy.stepSeconds", StepSeconds, 10, 3600, 30, w),
        FastStepSeconds = EngineConfig.Clamp("policy.fastStepSeconds", FastStepSeconds, 4, 600, 10, w),
        IncreaseFactor = EngineConfig.Clamp("policy.increaseFactor", IncreaseFactor, 1.1, 4, 1.5, w),
        FloorMargin = EngineConfig.Clamp("policy.floorMargin", FloorMargin, 1.0, 2.0, 1.1, w),
        FloorTtlHours = EngineConfig.Clamp("policy.floorTtlHours", FloorTtlHours, 1, 24 * 30, 24, w),
        AtCapRatio = EngineConfig.Clamp("policy.atCapRatio", AtCapRatio, 0.8, 1.0, 0.95, w),
        StallBudget = EngineConfig.Clamp("policy.stallBudget", StallBudget, 0.0001, 0.02, 0.001, w),
        DescendHeadroom = EngineConfig.Clamp("policy.descendHeadroom", DescendHeadroom, 0.1, 1.0, 0.5, w),
        SpikeMultiple = EngineConfig.Clamp("policy.spikeMultiple", SpikeMultiple, 2, 100, 10, w),
        RefaultCostUs = EngineConfig.Clamp("policy.refaultCostUs", RefaultCostUs, 1, 1000, 30, w),
        HardFaultCostUs = EngineConfig.Clamp("policy.hardFaultCostUs", HardFaultCostUs, 50, 50_000, 2000, w),
        HoldRetestMinutes = EngineConfig.Clamp("policy.holdRetestMinutes", HoldRetestMinutes, 1, 24 * 60, 15, w),
        MaxDepth = EngineConfig.Clamp("policy.maxDepth", MaxDepth, 0, 1, 1, w),
    };
}

/// <summary>Demand-aware governor thresholds.</summary>
public sealed class GovernorConfig
{
    /// <summary>Available physical memory below this percentage of total means at least medium pressure.</summary>
    public double MediumAvailPct { get; set; } = 25;
    public double HighAvailPct { get; set; } = 10;
    /// <summary>Commit charge above this percentage of the commit limit means at least medium pressure.</summary>
    public double MediumCommitPct { get; set; } = 80;
    public double HighCommitPct { get; set; } = 90;
    public double LowAggressiveness { get; set; } = 0.4;
    public double MediumAggressiveness { get; set; } = 0.75;
    public double HighAggressiveness { get; set; } = 1.0;
    /// <summary>Pressure must stay below a level this long before the governor steps down.</summary>
    public double DeescalateSeconds { get; set; } = 60;
    /// <summary>"low", "medium" or "high" forces a level (for benchmarks); null means automatic.</summary>
    public string? PressureOverride { get; set; }

    public GovernorConfig Sanitize(List<string> w)
    {
        var g = new GovernorConfig
        {
            MediumAvailPct = EngineConfig.Clamp("governor.mediumAvailPct", MediumAvailPct, 5, 80, 25, w),
            HighAvailPct = EngineConfig.Clamp("governor.highAvailPct", HighAvailPct, 2, 50, 10, w),
            MediumCommitPct = EngineConfig.Clamp("governor.mediumCommitPct", MediumCommitPct, 50, 99, 80, w),
            HighCommitPct = EngineConfig.Clamp("governor.highCommitPct", HighCommitPct, 55, 100, 90, w),
            LowAggressiveness = EngineConfig.Clamp("governor.lowAggressiveness", LowAggressiveness, 0, 1, 0.4, w),
            MediumAggressiveness = EngineConfig.Clamp("governor.mediumAggressiveness", MediumAggressiveness, 0, 1, 0.75, w),
            HighAggressiveness = EngineConfig.Clamp("governor.highAggressiveness", HighAggressiveness, 0, 1, 1.0, w),
            DeescalateSeconds = EngineConfig.Clamp("governor.deescalateSeconds", DeescalateSeconds, 10, 3600, 60, w),
        };
        if (g.HighAvailPct >= g.MediumAvailPct)
        {
            w.Add("governor: highAvailPct must be below mediumAvailPct; using defaults");
            g.HighAvailPct = 10; g.MediumAvailPct = 25;
        }
        if (g.HighCommitPct <= g.MediumCommitPct)
        {
            w.Add("governor: highCommitPct must be above mediumCommitPct; using defaults");
            g.HighCommitPct = 90; g.MediumCommitPct = 80;
        }
        if (g.MediumAggressiveness < g.LowAggressiveness)
        {
            w.Add("governor: mediumAggressiveness raised to lowAggressiveness");
            g.MediumAggressiveness = g.LowAggressiveness;
        }
        if (g.HighAggressiveness < g.MediumAggressiveness)
        {
            w.Add("governor: highAggressiveness raised to mediumAggressiveness");
            g.HighAggressiveness = g.MediumAggressiveness;
        }
        if (PressureOverride is not null)
        {
            if (Governor.TryParsePressure(PressureOverride, out var level)) g.PressureOverride = Governor.Name(level);
            else w.Add($"governor.pressureOverride: '{PressureOverride}' is not low/medium/high; ignored");
        }
        return g;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EngineConfig))]
[JsonSerializable(typeof(LearnedProfileFile))]
internal sealed partial class CoreJson : JsonSerializerContext;
