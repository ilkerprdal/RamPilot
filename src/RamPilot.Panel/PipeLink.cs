using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RamPilot.Panel;

/// <summary>Talks to the engine over <c>\\.\pipe\RamPilot.&lt;SessionId&gt;</c> (docs/IPC.md, protocol v2).</summary>
internal sealed class PipeLink : IEngineLink
{
    public const int ProtocolVersion = 2;
    private const string HelloRequest = "{\"cmd\":\"hello\",\"protocol\":2}";
    private const string StatusRequest = "{\"cmd\":\"status\"}";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
    private readonly string _pipeName;
    private volatile string _state = "";

    public PipeLink(string pipeName) => _pipeName = pipeName;

    public event Action<string>? Message;

    /// <summary>The engine's pipe name for the current Windows session.</summary>
    public static string DefaultPipeName()
    {
        using var self = Process.GetCurrentProcess();
        return "RamPilot." + self.SessionId.ToString(CultureInfo.InvariantCulture);
    }

    public void Send(string commandJson) => _commands.Writer.TryWrite(commandJson);

    public void StartEngine()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "RamPilot.exe");
        if (!File.Exists(exe))
        {
            Post("down", "engine_exe_missing", AppContext.BaseDirectory);
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Post("starting");
        }
        catch (Win32Exception ex)
        {
            Post("down", "engine_start_failed", ex.Message);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Post("connecting");
        while (!ct.IsCancellationRequested)
        {
            // CurrentUserOnly: only connect to a server owned by this user.
            // Identification: the server may identify us but never impersonate us.
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(800, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // No pipe (timeout), busy, or owned by someone else.
                EngineDown();
                await DelayQuiet(1500, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var reader = new StreamReader(pipe, Utf8, false, 64 * 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

                string hello = await RequestAsync(reader, writer, HelloRequest, ct).ConfigureAwait(false);
                if (!CheckProtocol(hello, requireOk: true))
                {
                    // Keep retrying slowly: the engine may be updated while the panel is open.
                    await DelayQuiet(5000, ct).ConfigureAwait(false);
                    continue;
                }
                _state = "up";

                while (!ct.IsCancellationRequested)
                {
                    while (_commands.Reader.TryRead(out var cmd))
                    {
                        string reply = await RequestAsync(reader, writer, cmd, ct).ConfigureAwait(false);
                        Message?.Invoke(Envelopes.ResultFromReply(cmd, reply));
                    }

                    string status = await RequestAsync(reader, writer, StatusRequest, ct).ConfigureAwait(false);
                    if (!CheckProtocol(status, requireOk: false))
                    {
                        break;
                    }
                    Message?.Invoke(Envelopes.Status(status));

                    // Wait one second, or less if a command arrives.
                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tick.CancelAfter(1000);
                    try
                    {
                        await _commands.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidDataException or ObjectDisposedException)
            {
                EngineDown();
                await DelayQuiet(1000, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Validates a hello reply or a Status object. On mismatch posts the "mismatch" state and returns false.
    /// Throws <see cref="InvalidDataException"/> for replies that are not JSON objects.
    /// </summary>
    private bool CheckProtocol(string json, bool requireOk)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("reply is not an object");
        }

        int? protocol = root.TryGetProperty("protocol", out var p) && p.TryGetInt32(out int pv) ? pv : null;
        string? version = root.TryGetProperty("engineVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool ok = !requireOk || (root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True);

        if (ok && protocol == ProtocolVersion)
        {
            return true;
        }
        // An engine that answers but does not accept hello (or reports another protocol) needs an update.
        _state = "mismatch";
        Message?.Invoke(Envelopes.Mismatch(protocol, version));
        return false;
    }

    private static async Task<string> RequestAsync(StreamReader reader, StreamWriter writer, string json, CancellationToken ct)
    {
        await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        string? line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(ReplyTimeout, ct).ConfigureAwait(false);
        return line ?? throw new IOException("engine closed the connection");
    }

    private void EngineDown()
    {
        // Reject commands queued while the engine is unreachable.
        while (_commands.Reader.TryRead(out var cmd))
        {
            Message?.Invoke(Envelopes.ResultFromReply(cmd, "{\"ok\":false,\"error\":\"engine_down\"}"));
        }
        // While starting, failed attempts must not flip the page back to "down"; the page times out itself.
        if (_state == "starting")
        {
            return;
        }
        Post("down");
    }

    private void Post(string state, string? error = null, string? detail = null)
    {
        _state = state;
        Message?.Invoke(Envelopes.Engine(state, error, detail));
    }

    private static async Task DelayQuiet(int ms, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ms, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
