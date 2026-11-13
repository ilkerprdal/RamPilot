using Microsoft.Win32;

namespace RamPilot;

/// <summary>Persistent user choices made from the panel: apps switched off, and start with Windows.</summary>
internal static class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "RamPilot";

    public static HashSet<string> LoadDisabled(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
                foreach (var l in File.ReadAllLines(path))
                    if (l.Trim().Length > 0) set.Add(l.Trim());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return set;
    }

    /// <returns>False if the file could not be written.</returns>
    public static bool SaveDisabled(string path, IEnumerable<string> exes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, exes);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool Autostart
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(RunName) is string s && s.Contains(AppInfo.ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <returns>False if the registry could not be written.</returns>
    public static bool SetAutostart(bool value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{AppInfo.ExePath}\"");
            else k.DeleteValue(RunName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
