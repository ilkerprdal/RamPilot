using System.Globalization;
using System.Text;

namespace RamPilot.Safety;

/// <summary>
/// Crash journal: every process instance (pid + create time) that RamPilot may have capped. An entry is written and
/// flushed to disk BEFORE the first cap is applied, so a crash at any point leaves a record for the guardian and
/// for the next start. Stale entries are harmless (recovery re-checks create time and the kernel flag).
/// </summary>
internal sealed class Journal
{
    private readonly string _path;
    private readonly Dictionary<(uint Pid, long Create), string> _entries = [];
    private bool _dirty;

    public Journal(string path) => _path = path;

    public int Count => _entries.Count;

    /// <summary>Ensures the instance is journaled on disk. False if the journal cannot be written (do not cap).</summary>
    public bool Ensure(uint pid, long create, string exe)
    {
        if (_entries.ContainsKey((pid, create))) return true;
        _entries[(pid, create)] = exe;
        if (Write()) return true;
        _entries.Remove((pid, create));
        return false;
    }

    /// <summary>The instance no longer needs a record (released or exited); rewritten lazily.</summary>
    public void Forget(uint pid, long create)
    {
        if (_entries.Remove((pid, create))) _dirty = true;
    }

    public void FlushIfDirty()
    {
        if (_dirty && Write()) _dirty = false;
    }

    public void Delete()
    {
        _entries.Clear();
        _dirty = false;
        try { File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var sb = new StringBuilder();
            foreach (var kv in _entries)
                sb.Append(kv.Key.Pid.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(kv.Key.Create.ToString(CultureInfo.InvariantCulture)).Append('|').Append(kv.Value).Append('\n');
            string tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static List<(uint Pid, long Create, string Exe)> Read(string path)
    {
        var list = new List<(uint, long, string)>();
        try
        {
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('|');
                if (parts.Length >= 2
                    && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid)
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long create))
                    list.Add((pid, create, parts.Length > 2 ? parts[2] : ""));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }
}
