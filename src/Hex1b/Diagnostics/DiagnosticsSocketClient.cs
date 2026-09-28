using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Hex1b.Diagnostics;

/// <summary>
/// The one client of the diagnostics socket protocol, shared by the CLI and MCP server so
/// both translate transport failures into the same diagnostic outcomes.
/// </summary>
internal sealed class DiagnosticsSocketClient
{
    /// <summary>Default limit for one request/response exchange.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const string EmptyResponseError = "Empty response from terminal";

    private readonly TimeSpan _timeout;

    public DiagnosticsSocketClient()
        : this(DefaultTimeout)
    {
    }

    internal DiagnosticsSocketClient(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>
    /// Sends one request and returns the target's response. Transport failures throw; a target
    /// that does not answer within the client's timeout throws <see cref="TimeoutException"/>.
    /// </summary>
    public async Task<DiagnosticsResponse> SendAsync(string socketPath, DiagnosticsRequest request, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            return await ExchangeOnceAsync(socketPath, request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The diagnostics target '{socketPath}' did not respond within {_timeout.TotalSeconds:0.#} seconds.");
        }
    }

    private static async Task<DiagnosticsResponse> ExchangeOnceAsync(string socketPath, DiagnosticsRequest request, CancellationToken ct)
    {

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

        var requestJson = JsonSerializer.Serialize(request, DiagnosticsJsonContext.Default.DiagnosticsRequest);
        await writer.WriteLineAsync(requestJson.AsMemory(), ct).ConfigureAwait(false);

        var responseLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(responseLine))
            return new DiagnosticsResponse { Success = false, Error = EmptyResponseError };

        return JsonSerializer.Deserialize(responseLine, DiagnosticsJsonContext.Default.DiagnosticsResponse)
            ?? new DiagnosticsResponse { Success = false, Error = "Failed to deserialize response" };
    }

    /// <summary>
    /// Probes a terminal socket to check if it is alive. Returns <c>null</c> when it is not.
    /// </summary>
    public async Task<DiagnosticsResponse?> TryProbeAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            return await SendAsync(socketPath, new DiagnosticsRequest { Method = "info" }, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Captures an attached target. Transport and protocol failures become diagnostic outcomes;
    /// only caller cancellation throws.
    /// </summary>
    public async Task<DiagnosticCaptureResult> CaptureAsync(
        string socketPath, DiagnosticCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = TerminalDiagnostics.CaptureOperation, Capture = request }, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.Problem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.Capture is { } result)
            return result;
        var (outcome, code, message) = Unexpected(response);
        return TerminalDiagnostics.Problem(outcome, code, message);
    }

    /// <summary>
    /// Describes an attached target's capabilities. Transport and protocol failures become
    /// diagnostic outcomes; only caller cancellation throws.
    /// </summary>
    public async Task<DiagnosticCapabilities> GetCapabilitiesAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = "capabilities" }, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.CapabilitiesProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.Capabilities is { } capabilities)
            return capabilities;
        var (outcome, code, message) = Unexpected(response);
        return TerminalDiagnostics.CapabilitiesProblem(outcome, code, message);
    }

    private static (DiagnosticOutcome, string, string) Unexpected(DiagnosticsResponse response) =>
        response.Error == EmptyResponseError
            ? (DiagnosticOutcome.Failed, "protocol-error", "The target closed the connection without a response.")
            : (DiagnosticOutcome.Failed, "incompatible-target", IncompatibleMessage(response));

    private static string IncompatibleMessage(DiagnosticsResponse response) =>
        "The target did not return a diagnostic contract result" +
        (response.Error is { } error ? $": {error}" : ". It may be running an older Hex1b build.");

    private async Task<(DiagnosticsResponse? Response, (DiagnosticOutcome Outcome, string Code, string Message)? Problem)> ExchangeAsync(
        string socketPath, DiagnosticsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return (await SendAsync(socketPath, request, cancellationToken).ConfigureAwait(false), null);
        }
        catch (TimeoutException error)
        {
            return (null, (DiagnosticOutcome.Failed, "timeout", error.Message));
        }
        catch (SocketException error)
        {
            return (null, (DiagnosticOutcome.Unavailable, "target-unreachable",
                $"Cannot connect to the diagnostics socket '{socketPath}': {error.Message}"));
        }
        catch (JsonException error)
        {
            return (null, (DiagnosticOutcome.Failed, "protocol-error", $"The target sent a malformed response: {error.Message}"));
        }
        catch (IOException error)
        {
            return (null, (DiagnosticOutcome.Failed, "transport-failed", error.Message));
        }
    }
}
