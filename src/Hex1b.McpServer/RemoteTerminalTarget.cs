using System.Diagnostics;
using Hex1b.Diagnostics;

namespace Hex1b.McpServer;

/// <summary>
/// A terminal target connected to a remote Hex1b application via Unix domain socket.
/// </summary>
public sealed class RemoteTerminalTarget : ITerminalTarget
{
    private readonly DiagnosticsSocketClient _client = new();
    private readonly string _socketPath;
    private readonly string _id;
    private bool _disposed;

    /// <summary>
    /// Gets the socket path for this remote target.
    /// </summary>
    public string SocketPath => _socketPath;

    /// <inheritdoc />
    public string Id => _id;

    /// <inheritdoc />
    public TerminalTargetType TargetType => TerminalTargetType.Remote;

    /// <inheritdoc />
    public int Width { get; private set; }

    /// <inheritdoc />
    public int Height { get; private set; }

    /// <inheritdoc />
    public int ProcessId { get; private set; }

    /// <inheritdoc />
    public bool IsAlive => !_disposed && IsProcessRunning(ProcessId);

    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; private set; }

    /// <inheritdoc />
    public string Name { get; private set; } = "Unknown";

    private RemoteTerminalTarget(string socketPath, int processId)
    {
        _socketPath = socketPath;
        ProcessId = processId;
        _id = $"remote-{processId}";
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Connects to a remote Hex1b application via its diagnostics socket.
    /// </summary>
    /// <param name="socketPath">Path to the Unix domain socket.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A connected remote terminal target.</returns>
    public static async Task<RemoteTerminalTarget> ConnectAsync(string socketPath, CancellationToken ct = default)
    {
        // Extract PID from socket path (format: [pid].diagnostics.socket)
        var fileName = Path.GetFileName(socketPath);
        var pidStr = fileName.Replace(".diagnostics.socket", "");
        if (!int.TryParse(pidStr, out var pid))
        {
            throw new ArgumentException($"Cannot extract PID from socket path: {socketPath}");
        }

        var target = new RemoteTerminalTarget(socketPath, pid);
        await target.RefreshInfoAsync(ct);
        return target;
    }

    /// <summary>
    /// Connects to a remote Hex1b application by process ID.
    /// </summary>
    /// <param name="processId">The process ID of the Hex1b application.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A connected remote terminal target.</returns>
    public static Task<RemoteTerminalTarget> ConnectByPidAsync(int processId, CancellationToken ct = default)
    {
        var socketPath = GetSocketPath(processId);
        return ConnectAsync(socketPath, ct);
    }

    /// <summary>
    /// Gets the socket path for a given process ID.
    /// </summary>
    public static string GetSocketPath(int pid)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".hex1b", "sockets", $"{pid}.diagnostics.socket");
    }

    /// <summary>
    /// Refreshes the info from the remote target.
    /// </summary>
    public async Task RefreshInfoAsync(CancellationToken ct = default)
    {
        var response = await _client.SendAsync(_socketPath, new DiagnosticsRequest { Method = "info" }, ct);
        if (response.Success)
        {
            Name = response.AppName ?? "Unknown";
            Width = response.Width ?? 0;
            Height = response.Height ?? 0;
            if (response.StartTime.HasValue)
            {
                StartedAt = response.StartTime.Value;
            }
        }
    }

    /// <inheritdoc />
    public async Task SendInputAsync(string text, CancellationToken ct = default)
    {
        await _client.SendAsync(_socketPath, new DiagnosticsRequest { Method = "input", Data = text }, ct);
    }

    /// <inheritdoc />
    public async Task SendKeyAsync(string key, string[]? modifiers = null, CancellationToken ct = default)
    {
        // Use the "key" method which injects proper key events via the workload adapter
        await _client.SendAsync(_socketPath, new DiagnosticsRequest { Method = "key", Key = key, Modifiers = modifiers }, ct);
    }

    /// <inheritdoc />
    public async Task SendMouseClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        // Use the "click" method which injects proper mouse events via the workload adapter
        var buttonStr = button switch
        {
            MouseButton.Left => "left",
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => "left"
        };
        
        await _client.SendAsync(_socketPath, new DiagnosticsRequest { Method = "click", X = x, Y = y, Button = buttonStr }, ct);
    }

    /// <inheritdoc />
    public Task<DiagnosticCaptureResult> CaptureAsync(DiagnosticCaptureRequest request, CancellationToken ct = default)
        => _client.CaptureAsync(_socketPath, request, ct);

    /// <inheritdoc />
    public Task<DiagnosticCapabilities> GetDiagnosticCapabilitiesAsync(CancellationToken ct = default)
        => _client.GetCapabilitiesAsync(_socketPath, ct);

    /// <inheritdoc />
    public async Task<bool> WaitForTextAsync(string text, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var capture = await CaptureAsync(new DiagnosticCaptureRequest { Format = DiagnosticCaptureFormat.Text }, cts.Token);
                if (capture.Content?.Contains(text) == true)
                    return true;

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout occurred
        }

        return false;
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
