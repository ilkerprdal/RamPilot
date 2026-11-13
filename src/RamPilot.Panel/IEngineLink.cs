namespace RamPilot.Panel;

/// <summary>
/// Connection between the panel and the engine. Every message sent to the page is a ready JSON envelope:
/// <code>
///   {"type":"status","data":{Status}}
///   {"type":"engine","state":"connecting|down|starting|mismatch","error":"code","detail":"...",
///    "engineProtocol":3,"engineVersion":"0.3.0","panelProtocol":2}
///   {"type":"result","cmd":"pause","ok":true,"error":null}
/// </code>
/// Error values are machine-readable codes; the page localizes them.
/// </summary>
internal interface IEngineLink
{
    event Action<string>? Message;

    /// <summary>A validated single-line command JSON coming from the page.</summary>
    void Send(string commandJson);

    /// <summary>Starts RamPilot.exe from the panel's own folder.</summary>
    void StartEngine();

    Task RunAsync(CancellationToken ct);
}
