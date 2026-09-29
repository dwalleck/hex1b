using Hex1b.Diagnostics;

namespace Hex1b.McpServer;

/// <summary>
/// Mouse button for click operations.
/// </summary>
public enum MouseButton
{
    /// <summary>Left mouse button.</summary>
    Left = 0,
    /// <summary>Middle mouse button.</summary>
    Middle = 1,
    /// <summary>Right mouse button.</summary>
    Right = 2
}

/// <summary>
/// Abstraction for terminal targets that can be either local (launched by MCP server)
/// or remote (connected via Unix domain socket to a Hex1b app with diagnostics enabled).
/// </summary>
public interface ITerminalTarget : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this terminal target.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the type of terminal target.
    /// </summary>
    TerminalTargetType TargetType { get; }

    /// <summary>
    /// Gets the terminal width in columns.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the terminal height in rows.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Gets the process ID of the terminal process.
    /// </summary>
    int ProcessId { get; }

    /// <summary>
    /// Gets whether this target is still connected/running.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// Gets when this target was created/connected.
    /// </summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Gets the application or command name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Sends text input to the terminal.
    /// </summary>
    /// <returns>The input ids the send was assigned, or <see langword="null"/> when the target does not track input.</returns>
    Task<DiagnosticAcceptedInput?> SendInputAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Sends a special key to the terminal.
    /// </summary>
    /// <returns>The input ids the send was assigned, or <see langword="null"/> when the target does not track input.</returns>
    Task<DiagnosticAcceptedInput?> SendKeyAsync(string key, string[]? modifiers = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a mouse click at the specified cell position.
    /// </summary>
    /// <param name="x">Column (0-based).</param>
    /// <param name="y">Row (0-based).</param>
    /// <param name="button">Mouse button to click.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The input ids the send was assigned, or <see langword="null"/> when the target does not track input.</returns>
    Task<DiagnosticAcceptedInput?> SendMouseClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default);

    /// <summary>
    /// Captures the terminal model through the shared diagnostic contract. Target, transport,
    /// and request problems are reported through the result's outcome rather than thrown.
    /// </summary>
    Task<DiagnosticCaptureResult> CaptureAsync(DiagnosticCaptureRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the target application's latest published frame through the shared
    /// application-frame contract; targets without an application layer report it unavailable.
    /// </summary>
    Task<DiagnosticApplicationFrameResult> CaptureApplicationFrameAsync(DiagnosticApplicationFrameRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the target's native delivery record through the shared delivery contract; targets
    /// without an observable native presentation report it unavailable.
    /// </summary>
    Task<DiagnosticDeliveryResult> CaptureDeliveryAsync(DiagnosticDeliveryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Describes the target's diagnostic capabilities and their limits.
    /// </summary>
    Task<DiagnosticCapabilities> GetDiagnosticCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Waits for specific text to appear on the terminal screen.
    /// </summary>
    Task<bool> WaitForTextAsync(string text, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// The type of terminal target.
/// </summary>
public enum TerminalTargetType
{
    /// <summary>
    /// A terminal session launched by the MCP server (child process).
    /// </summary>
    Local,

    /// <summary>
    /// A remote Hex1b application connected via Unix domain socket.
    /// </summary>
    Remote
}
