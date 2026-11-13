using System.Globalization;
using System.Reflection;
using System.Text;
using RamPilot.Core;

namespace RamPilot;

/// <summary>File locations. All local; RamPilot makes no network calls.</summary>
internal sealed class AppPaths
{
    public AppPaths(string? configOverride)
    {
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamPilot");
        ConfigPath = string.IsNullOrWhiteSpace(configOverride) ? Path.Combine(DataDir, "config.json") : Path.GetFullPath(configOverride);
        ConfigOverridden = !string.IsNullOrWhiteSpace(configOverride);
    }

    public string DataDir { get; }
    public string ConfigPath { get; }
    public bool ConfigOverridden { get; }
    public string JournalPath => Path.Combine(DataDir, "journal.txt");
    public string LearnedPath => Path.Combine(DataDir, "learned.json");
    public string DisabledPath => Path.Combine(DataDir, "disabled.txt");
    public string LogPath => Path.Combine(DataDir, "rampilot.log");
    public string GuardianLogPath => Path.Combine(DataDir, "guardian.log");
}

/// <summary>Thread-safe line logger (English). Rotates at 5 MB. Never throws.</summary>
internal sealed class Logger
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly bool _console;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public Logger(string path, bool console)
    {
        _path = path;
        _console = console;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 5_000_000) File.Move(path, path + ".1", true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Info(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + message;
        lock (_lock)
        {
            if (_console)
            {
                try { Console.WriteLine(line); } catch (IOException) { }
            }
            try { File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal static class ConfigStore
{
    /// <summary>
    /// Loads config.json, creating it with defaults if missing. A malformed file is never overwritten: defaults are
    /// used for this run and the problem is logged. Values are always clamped.
    /// </summary>
    public static EngineConfig Load(AppPaths paths, Action<string> log)
    {
        EngineConfig raw;
        try
        {
            if (File.Exists(paths.ConfigPath))
            {
                raw = EngineConfig.FromJson(File.ReadAllText(paths.ConfigPath));
            }
            else
            {
                raw = new EngineConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(paths.ConfigPath)!);
                File.WriteAllText(paths.ConfigPath, raw.ToJson());
                log($"config: created {paths.ConfigPath} with defaults");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log($"config: cannot use {paths.ConfigPath} ({ex.Message}); using defaults for this run");
            raw = new EngineConfig();
        }
        var warnings = new List<string>();
        var cfg = raw.Sanitize(warnings);
        foreach (var w in warnings) log("config: " + w);
        return cfg;
    }
}

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RamPilot.exe");

    public static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    public static string Mb(ulong bytes) => Units.ToMb(bytes).ToString("F1", CultureInfo.InvariantCulture);
}
