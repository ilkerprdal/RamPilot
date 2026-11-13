namespace RamPilot.Core;

/// <param name="Role">Chromium process role ("main", "renderer", "utility:network.mojom.NetworkService", ...).</param>
/// <param name="Excluded">True if the process must never be capped.</param>
/// <param name="Reason">Why it is excluded (for logs), or null.</param>
public readonly record struct ProcessClass(string Role, bool Excluded, string? Reason);

/// <summary>
/// Classifies a process from its command line. Fail-closed: if the command line could not be read, the process
/// is excluded, because GPU/audio/crashpad helpers are only recognizable by their command line.
/// </summary>
public static class RoleClassifier
{
    /// <summary>Built-in exclusions: latency-critical or tiny helpers.</summary>
    public static readonly IReadOnlyList<string> BuiltInExcludes =
    [
        "--type=gpu-process",
        "--utility-sub-type=audio",
        "audio.mojom",
        "--type=crashpad-handler",
        "crashpad",
    ];

    public static ProcessClass Classify(string? commandLine, IReadOnlyList<string>? extraExcludes = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return new ProcessClass("unknown", true, "command line unavailable");

        string role = RoleOf(commandLine);
        foreach (var marker in BuiltInExcludes)
            if (commandLine.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return new ProcessClass(role, true, marker);
        if (extraExcludes is not null)
            foreach (var marker in extraExcludes)
                if (marker.Length > 0 && commandLine.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return new ProcessClass(role, true, marker);
        return new ProcessClass(role, false, null);
    }

    public static string RoleOf(string commandLine)
    {
        string? type = SwitchValue(commandLine, "--type=");
        if (type is null) return "main";
        if (type == "utility")
        {
            string? sub = SwitchValue(commandLine, "--utility-sub-type=");
            return sub is null ? "utility" : "utility:" + sub;
        }
        return type;
    }

    /// <summary>Value of a "--name=value" switch that starts an argument (start of string, or after whitespace).</summary>
    private static string? SwitchValue(string cmd, string prefix)
    {
        int from = 0;
        while (true)
        {
            int i = cmd.IndexOf(prefix, from, StringComparison.Ordinal);
            if (i < 0) return null;
            if (i == 0 || char.IsWhiteSpace(cmd[i - 1]) || cmd[i - 1] == '"')
            {
                int start = i + prefix.Length;
                int end = start;
                while (end < cmd.Length && !char.IsWhiteSpace(cmd[end]) && cmd[end] != '"') end++;
                return end > start ? cmd[start..end] : null;
            }
            from = i + prefix.Length;
        }
    }
}
