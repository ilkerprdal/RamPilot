using System.IO.Pipes;
using System.Text;

namespace RamPilot.Panel;

/// <summary>
/// Test helper for <c>--demo-mismatch</c>: a pipe server that behaves like a newer engine and rejects
/// the panel's hello with <c>protocol_mismatch</c>. Exercises the real <see cref="PipeLink"/> code path.
/// </summary>
internal sealed class FakeEngineServer : IDisposable
{
    private const string Reply = "{\"ok\":false,\"error\":\"protocol_mismatch\",\"protocol\":3,\"engineVersion\":\"0.3.0\"}";
    private readonly CancellationTokenSource _cts = new();

    public FakeEngineServer()
    {
        PipeName = "RamPilot.MismatchTest." + Environment.ProcessId;
        _ = Task.Run(() => ServeAsync(_cts.Token));
    }

    public string PipeName { get; }

    private async Task ServeAsync(CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, utf8);
                using var writer = new StreamWriter(server, utf8) { NewLine = "\n", AutoFlush = true };
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is not null)
                {
                    await writer.WriteLineAsync(Reply.AsMemory(), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client went away; accept the next one.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
