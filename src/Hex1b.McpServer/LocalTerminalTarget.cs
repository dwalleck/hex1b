using Hex1b.Diagnostics;

namespace Hex1b.McpServer;

/// <summary>
/// A terminal target wrapping a local terminal session (child process launched by MCP server).
/// </summary>
public sealed class LocalTerminalTarget : ITerminalTarget
{
    private readonly TerminalSession _session;

    /// <summary>
    /// Creates a new local terminal target from an existing session.
    /// </summary>
    public LocalTerminalTarget(TerminalSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public string Id => _session.Id;

    /// <inheritdoc />
    public TerminalTargetType TargetType => TerminalTargetType.Local;

    /// <inheritdoc />
    public int Width => _session.Width;

    /// <inheritdoc />
    public int Height => _session.Height;

    /// <inheritdoc />
    public int ProcessId => _session.ProcessId;

    /// <inheritdoc />
    public bool IsAlive => !_session.HasExited;

    /// <inheritdoc />
    public DateTimeOffset StartedAt => _session.StartedAt;

    /// <inheritdoc />
    public string Name => _session.Command;

    /// <summary>
    /// Gets the underlying terminal session.
    /// </summary>
    public TerminalSession Session => _session;

    /// <inheritdoc />
    public Task<DiagnosticAcceptedInput?> SendInputAsync(string text, CancellationToken ct = default)
        => _session.SendInputAsync(text, ct);

    /// <inheritdoc />
    public Task<DiagnosticAcceptedInput?> SendKeyAsync(string key, string[]? modifiers = null, CancellationToken ct = default)
        => _session.SendKeyAsync(key, modifiers, ct);

    /// <inheritdoc />
    public async Task<DiagnosticAcceptedInput?> SendMouseClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        // SGR mouse encoding: ESC [ < button ; column ; row M (press) m (release)
        // Columns and rows are 1-based in the protocol
        var col = x + 1;
        var row = y + 1;
        var btn = (int)button;
        
        var mouseSequence = $"\x1b[<{btn};{col};{row}M\x1b[<{btn};{col};{row}m";
        return await _session.SendInputAsync(mouseSequence, ct);
    }

    /// <inheritdoc />
    public Task<DiagnosticCaptureResult> CaptureAsync(DiagnosticCaptureRequest request, CancellationToken ct = default)
        => _session.Diagnostics.CaptureAsync(request, ct);

    /// <inheritdoc />
    public Task<DiagnosticApplicationFrameResult> CaptureApplicationFrameAsync(DiagnosticApplicationFrameRequest request, CancellationToken ct = default)
        => _session.Diagnostics.CaptureApplicationFrameAsync(request, ct);

    public Task<DiagnosticDeliveryResult> CaptureDeliveryAsync(DiagnosticDeliveryRequest request, CancellationToken ct = default)
        => Task.FromResult(_session.Diagnostics.CaptureDelivery(request));

    public Task<DiagnosticCaseResult> StartCaseAsync(DiagnosticCaseStartRequest request, CancellationToken ct = default)
        => Task.FromResult(_session.Diagnostics.StartCase(request));

    public Task<DiagnosticCaseResult> StopCaseAsync(CancellationToken ct = default)
        => _session.Diagnostics.StopCaseAsync(ct);

    public Task<DiagnosticCaseResult> GetCaseStatusAsync(CancellationToken ct = default)
        => Task.FromResult(_session.Diagnostics.GetCaseStatus());

    public Task<DiagnosticCaseMarkResult> MarkCaseAsync(string? label, CancellationToken ct = default)
        => Task.FromResult(_session.Diagnostics.MarkCase(label));

    public Task<DiagnosticCaseRecoverResult> RecoverCaseAsync(string? label, CancellationToken ct = default)
        => Task.FromResult(_session.Diagnostics.RecoverCase(label));

    /// <inheritdoc />
    public Task<DiagnosticCapabilities> GetDiagnosticCapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult(_session.Diagnostics.GetCapabilities());

    /// <inheritdoc />
    public Task<bool> WaitForTextAsync(string text, TimeSpan timeout, CancellationToken ct = default)
        => _session.WaitForTextAsync(text, timeout, ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
