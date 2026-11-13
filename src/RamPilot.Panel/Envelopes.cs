using System.Text.Json;
using System.Text.Json.Nodes;

namespace RamPilot.Panel;

/// <summary>Builds the JSON envelopes the page understands (see <see cref="IEngineLink"/>).</summary>
internal static class Envelopes
{
    public static string Status(string statusJson) => "{\"type\":\"status\",\"data\":" + statusJson + "}";

    public static string Engine(string state, string? error = null, string? detail = null) => new JsonObject
    {
        ["type"] = "engine",
        ["state"] = state,
        ["error"] = error,
        ["detail"] = detail,
    }.ToJsonString();

    public static string Mismatch(int? engineProtocol, string? engineVersion) => new JsonObject
    {
        ["type"] = "engine",
        ["state"] = "mismatch",
        ["error"] = "protocol_mismatch",
        ["engineProtocol"] = engineProtocol,
        ["engineVersion"] = engineVersion,
        ["panelProtocol"] = PipeLink.ProtocolVersion,
    }.ToJsonString();

    public static string Result(string? cmd, bool ok, string? error) => new JsonObject
    {
        ["type"] = "result",
        ["cmd"] = cmd,
        ["ok"] = ok,
        ["error"] = error,
    }.ToJsonString();

    /// <summary>Turns an engine reply line into a result envelope for the command that caused it.</summary>
    public static string ResultFromReply(string commandJson, string reply)
    {
        string? cmd = null;
        try
        {
            cmd = JsonNode.Parse(commandJson)?["cmd"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        bool ok = false;
        string? error;
        try
        {
            var r = JsonNode.Parse(reply) as JsonObject;
            ok = r?["ok"]?.GetValueKind() == JsonValueKind.True;
            error = r?["error"] is JsonValue e && e.GetValueKind() == JsonValueKind.String ? e.GetValue<string>() : null;
        }
        catch (JsonException)
        {
            error = "bad_reply";
        }
        return Result(cmd, ok, error);
    }
}
