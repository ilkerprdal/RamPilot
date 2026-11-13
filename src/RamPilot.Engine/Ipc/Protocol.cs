using System.Text.Json.Serialization;

namespace RamPilot.Ipc;

// docs/IPC.md, protocol v2. Field names are camelCase. No human-readable UI text travels over the pipe.

internal static class ProtocolInfo
{
    public const int Version = 2;
}

internal sealed class StatusDto
{
    public int Protocol { get; set; } = ProtocolInfo.Version;
    public string EngineVersion { get; set; } = "";
    public bool Paused { get; set; }
    public bool Autostart { get; set; }
    public long UptimeSec { get; set; }
    public EngineDto Engine { get; set; } = new();
    public SystemDto System { get; set; } = new();
    public GovernorDto Governor { get; set; } = new();
    public SavingsDto Savings { get; set; } = new();
    public List<AppDto> Apps { get; set; } = [];
    public List<EventDto> Events { get; set; } = [];
    public List<HistoryDto> History { get; set; } = [];
}

internal sealed class EngineDto
{
    public double WsMb { get; set; }
    public double PrivateMb { get; set; }
}

internal sealed class SystemDto
{
    public double TotalMb { get; set; }
    public double AvailMb { get; set; }
    public double CompressionMb { get; set; }
    public int LoadPct { get; set; }
    public int CommitPct { get; set; }
}

internal sealed class GovernorDto
{
    public string Pressure { get; set; } = "low";
    public double Aggressiveness { get; set; }
}

internal sealed class SavingsDto
{
    public double ReclaimedMb { get; set; }
    public double StoreCostMb { get; set; }
    public double NetMb { get; set; }
}

internal sealed class AppDto
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public bool Focused { get; set; }
    public bool Audio { get; set; }
    public string State { get; set; } = "NotRunning";
    public int ProcCount { get; set; }
    public double WsMb { get; set; }
    public double PrivateWsMb { get; set; }
    public double CapMb { get; set; }
    public double ReclaimedMb { get; set; }
}

internal sealed class EventDto
{
    public long T { get; set; }
    public string Kind { get; set; } = "info";
    public string Code { get; set; } = "";
    public string? App { get; set; }
    public string? Role { get; set; }
    public double? CapMb { get; set; }
    public double? PrevCapMb { get; set; }
    public string? Reason { get; set; }
    public int? Count { get; set; }
    public string? Detail { get; set; }
}

internal sealed class HistoryDto
{
    public long T { get; set; }
    public double AvailMb { get; set; }
    public double ManagedMb { get; set; }
    public double NetMb { get; set; }
}

internal sealed class RequestDto
{
    public string Cmd { get; set; } = "";
    public int? Protocol { get; set; }
    public string? Exe { get; set; }
    public bool? Enabled { get; set; }
}

internal sealed class ReplyDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int? Protocol { get; set; }
    public string? EngineVersion { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(RequestDto))]
[JsonSerializable(typeof(ReplyDto))]
internal sealed partial class IpcJson : JsonSerializerContext;
