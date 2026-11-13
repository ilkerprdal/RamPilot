using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RamPilot.Win32;

namespace RamPilot.Ipc;

/// <summary>
/// Named-pipe server, protocol v2 (docs/IPC.md). Pipe name is per Windows session (\\.\pipe\RamPilot.&lt;session&gt;),
/// only the current user may connect, at most <see cref="MaxInstances"/> concurrent connections, a request line
/// longer than <see cref="MaxLineBytes"/> closes the connection, and an idle connection is dropped after 2 minutes.
/// </summary>
internal sealed class IpcServer : IDisposable
{
    public const int MaxInstances = 4;
    public const int MaxLineBytes = 4096;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    /// <summary>A client that stops reading must not pin a connection slot.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<RequestDto, Task<string>> _handler;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _slots = new(MaxInstances, MaxInstances);
    private readonly CancellationTokenSource _cts = new();
    private Task? _accept;

    public IpcServer(Func<RequestDto, Task<string>> handler, Action<string> log)
    {
        _handler = handler;
        _log = log;
    }

    public static string PipeName
    {
        get
        {
            _ = Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint session);
            return "RamPilot." + session.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public void Start() => _accept = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        string name = PipeName;
        int consecutiveErrors = 0;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? s = null;
            try
            {
                await _slots.WaitAsync(_cts.Token).ConfigureAwait(false);
                s = new NamedPipeServerStream(name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous, 4096, 65536);
                await s.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                consecutiveErrors = 0;
                var conn = s;
                s = null;
                _ = Task.Run(() => ServeAsync(conn));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                s?.Dispose();
                _slots.Release();
                // Log the first failure of a streak only, then back off.
                if (consecutiveErrors++ == 0) _log($"ipc: {ex.Message}");
                try { await Task.Delay(Math.Min(30_000, 1000 * consecutiveErrors), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream s)
    {
        var buf = new byte[MaxLineBytes + 1];
        int used = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                idle.CancelAfter(IdleTimeout);
                int n = await s.ReadAsync(buf.AsMemory(used, buf.Length - used), idle.Token).ConfigureAwait(false);
                if (n == 0) break;
                used += n;
                int start = 0;
                for (int nl; (nl = Array.IndexOf(buf, (byte)'\n', start, used - start)) >= 0; start = nl + 1)
                {
                    string line = Encoding.UTF8.GetString(buf, start, nl - start).TrimEnd('\r');
                    string reply = await HandleLineAsync(line).ConfigureAwait(false);
                    byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    using var write = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    write.CancelAfter(WriteTimeout);
                    await s.WriteAsync(bytes, write.Token).ConfigureAwait(false);
                    await s.FlushAsync(write.Token).ConfigureAwait(false);
                }
                if (start > 0)
                {
                    Buffer.BlockCopy(buf, start, buf, 0, used - start);
                    used -= start;
                }
                if (used > MaxLineBytes) break;   // line too long: drop the connection
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // client went away, idle timeout, or shutdown
        }
        finally
        {
            try { s.Dispose(); } catch (IOException) { }
            _slots.Release();
        }
    }

    private async Task<string> HandleLineAsync(string line)
    {
        RequestDto? req;
        try { req = JsonSerializer.Deserialize(line, IpcJson.Default.RequestDto); }
        catch (JsonException) { req = null; }
        if (req is null || string.IsNullOrEmpty(req.Cmd)) return Error("bad_request");
        try
        {
            return await _handler(req).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
        {
            _log("ipc: handler failed: " + ex.Message);
            return Error("bad_request");
        }
    }

    public static string Error(string code) =>
        JsonSerializer.Serialize(new ReplyDto { Ok = false, Error = code }, IpcJson.Default.ReplyDto);

    public static string Ok() => """{"ok":true}""";

    public void Dispose()
    {
        _cts.Cancel();
        try { _accept?.Wait(1000); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
