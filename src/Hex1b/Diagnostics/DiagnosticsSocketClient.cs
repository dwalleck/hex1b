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
    /// <summary>Default limit for one observational exchange (capture or capabilities).</summary>
    internal static readonly TimeSpan DefaultObservationTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private const string EmptyResponseError = "Empty response from terminal";

    private readonly TimeSpan _observationTimeout;

    public DiagnosticsSocketClient()
        : this(DefaultObservationTimeout)
    {
    }

    internal DiagnosticsSocketClient(TimeSpan observationTimeout)
    {
        _observationTimeout = observationTimeout;
    }

    /// <summary>
    /// Sends one request and returns the target's response. Transport failures throw. With a
    /// <paramref name="timeout"/>, a target that does not answer in time throws
    /// <see cref="TimeoutException"/>. Without one, the exchange lasts until the caller cancels,
    /// so a non-idempotent request (input, resize, recording) is never reported failed while the
    /// target is still applying it.
    /// </summary>
    public async Task<DiagnosticsResponse> SendAsync(string socketPath, DiagnosticsRequest request,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        if (timeout is null)
            return await ExchangeOnceAsync(socketPath, request, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout.Value);
        try
        {
            return await ExchangeOnceAsync(socketPath, request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The diagnostics target '{socketPath}' did not respond within {timeout.Value.TotalSeconds:0.#} seconds.");
        }
    }

    /// <summary>
    /// Probes a terminal socket to check if it is alive. Returns <c>null</c> when it is not.
    /// </summary>
    public async Task<DiagnosticsResponse?> TryProbeAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync(socketPath, new DiagnosticsRequest { Method = "info" }, cancellationToken, ProbeTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Captures an attached target. Transport, protocol, and contract failures become diagnostic
    /// outcomes; only caller cancellation throws.
    /// </summary>
    public async Task<DiagnosticCaptureResult> CaptureAsync(
        string socketPath, DiagnosticCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = TerminalDiagnostics.CaptureOperation, Capture = request }, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.Problem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.Capture is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return TerminalDiagnostics.Problem(outcome, code, message);
        }

        return Validate(result);
    }

    /// <summary>
    /// Returns an attached application's latest published frame. Transport, protocol, and
    /// contract failures become diagnostic outcomes; only caller cancellation throws.
    /// </summary>
    public async Task<DiagnosticApplicationFrameResult> CaptureApplicationFrameAsync(
        string socketPath, DiagnosticApplicationFrameRequest request, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = TerminalDiagnostics.ApplicationFrameOperation, ApplicationFrame = request },
            cancellationToken).ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.FrameProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.ApplicationFrame is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return TerminalDiagnostics.FrameProblem(outcome, code, message);
        }

        return Validate(result);
    }

    /// <summary>
    /// Describes an attached target's capabilities. Transport, protocol, and contract failures
    /// become diagnostic outcomes; only caller cancellation throws.
    /// </summary>
    public async Task<DiagnosticCapabilities> GetCapabilitiesAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = "capabilities" }, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.CapabilitiesProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.Capabilities is not { } capabilities)
        {
            var (outcome, code, message) = Unexpected(response);
            return TerminalDiagnostics.CapabilitiesProblem(outcome, code, message);
        }

        return capabilities.ContractVersion == TerminalDiagnostics.ContractVersion
            ? capabilities
            : TerminalDiagnostics.CapabilitiesProblem(DiagnosticOutcome.Failed, "incompatible-target",
                VersionMessage(capabilities.ContractVersion));
    }

    // A remote result is trusted only when it speaks this contract version and a captured
    // outcome carries every field the contract promises for it.
    private static DiagnosticCaptureResult Validate(DiagnosticCaptureResult result)
    {
        // Deserialization leaves omitted top-level lists null despite their initializers, and a
        // malformed target can send null entries; neither reaches callers.
        result = result with
        {
            ContentCoverage = (result.ContentCoverage ?? []).Where(entry => entry is not null).ToArray(),
            UnavailableFields = (result.UnavailableFields ?? []).Where(entry => entry is not null).ToArray(),
            Limitations = (result.Limitations ?? []).Where(entry => entry is not null).ToArray(),
        };

        if (result.ContractVersion != TerminalDiagnostics.ContractVersion)
            return TerminalDiagnostics.Problem(DiagnosticOutcome.Failed, "incompatible-target", VersionMessage(result.ContractVersion));

        if (result.Outcome == DiagnosticOutcome.Captured &&
            (result.Content is null || result.Format is null || result.Geometry is null || result.History is null || result.Identity is null))
        {
            return TerminalDiagnostics.Problem(DiagnosticOutcome.Failed, "protocol-error",
                "The target reported a capture without its content, format, geometry, history, or identity.");
        }

        if (result.Outcome != DiagnosticOutcome.Captured && result.Problem is null)
        {
            return TerminalDiagnostics.Problem(DiagnosticOutcome.Failed, "protocol-error",
                $"The target reported outcome '{DiagnosticContractNames.Of(result.Outcome)}' without a problem.");
        }

        return result.Outcome == DiagnosticOutcome.Captured ? ExplainOmittedFields(result) : result;
    }

    private static DiagnosticApplicationFrameResult Validate(DiagnosticApplicationFrameResult result)
    {
        result = result with
        {
            ContentCoverage = (result.ContentCoverage ?? []).Where(entry => entry is not null).ToArray(),
            UnavailableFields = (result.UnavailableFields ?? []).Where(entry => entry is not null).ToArray(),
            Limitations = (result.Limitations ?? []).Where(entry => entry is not null).ToArray(),
        };

        if (result.ContractVersion != TerminalDiagnostics.ContractVersion)
            return TerminalDiagnostics.FrameProblem(DiagnosticOutcome.Failed, "incompatible-target", VersionMessage(result.ContractVersion));

        if (result.Outcome == DiagnosticOutcome.Captured && (result.Frame?.Focus is null || result.Identity is null))
        {
            return TerminalDiagnostics.FrameProblem(DiagnosticOutcome.Failed, "protocol-error",
                "The target reported an application frame without its frame, focus, or identity.");
        }

        if (result.Outcome != DiagnosticOutcome.Captured && result.Problem is null)
        {
            return TerminalDiagnostics.FrameProblem(DiagnosticOutcome.Failed, "protocol-error",
                $"The target reported outcome '{DiagnosticContractNames.Of(result.Outcome)}' without a problem.");
        }

        if (result.Frame is { } frame && !IsComplete(frame))
        {
            return TerminalDiagnostics.FrameProblem(DiagnosticOutcome.Failed, "protocol-error",
                "The target reported an application frame with missing lists (popups, focusables, children, carets, or selections).");
        }

        return result;
    }

    // Deserialization leaves omitted lists null despite their initializers; a frame the contract
    // promises complete is checked before callers walk it.
    private static bool IsComplete(DiagnosticApplicationFrame frame)
    {
        static bool EditorComplete(DiagnosticEditorState? editor) =>
            editor is null || (editor.Carets is not null && editor.Selections is not null && editor.Bounds is not null);

        static bool NodeComplete(DiagnosticFrameNode? node) =>
            node is not null && node.Children is not null && node.Bounds is not null && node.VisibleBounds is not null
            && EditorComplete(node.Editor) && node.Children.All(NodeComplete);

        return frame.Popups is not null && frame.Focus.Focusables is not null && EditorComplete(frame.FocusedEditor)
            && (frame.Root is null || NodeComplete(frame.Root));
    }

    // An older target may omit fields this contract version added. Report them as unavailable,
    // never as a default value, unless the target already explained the absence.
    private static DiagnosticCaptureResult ExplainOmittedFields(DiagnosticCaptureResult result)
    {
        var additions = new List<DiagnosticUnavailableField>();
        void Explain(bool absent, string field)
        {
            if (absent && !result.UnavailableFields.Any(f => f.Field == field))
                additions.Add(new DiagnosticUnavailableField
                {
                    Field = field,
                    Reason = "The target did not report this field; it may be running an older Hex1b build.",
                });
        }

        Explain(result.Identity!.ModelSequence is null, "identity.modelSequence");
        Explain(result.SynchronizedUpdate is null, "synchronizedUpdate");
        return additions.Count == 0 ? result : result with { UnavailableFields = [.. result.UnavailableFields, .. additions] };
    }

    private static string VersionMessage(int version) =>
        $"The target speaks diagnostic contract version {version}; this client supports version {TerminalDiagnostics.ContractVersion}.";

    private static (DiagnosticOutcome, string, string) Unexpected(DiagnosticsResponse response) =>
        response.Error == EmptyResponseError
            ? (DiagnosticOutcome.Failed, "protocol-error", "The target closed the connection without a response.")
            : (DiagnosticOutcome.Failed, "incompatible-target", IncompatibleMessage(response));

    private static string IncompatibleMessage(DiagnosticsResponse response) =>
        "The target did not return a diagnostic contract result" +
        (response.Error is { } error ? $": {error}" : ". It may be running an older Hex1b build.");

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

    private async Task<(DiagnosticsResponse? Response, (DiagnosticOutcome Outcome, string Code, string Message)? Problem)> ExchangeAsync(
        string socketPath, DiagnosticsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return (await SendAsync(socketPath, request, cancellationToken, _observationTimeout).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (Exception error)
        {
            // Includes I/O errors and invalid endpoints (for example a socket path too long for the platform).
            return (null, (DiagnosticOutcome.Failed, "transport-failed", $"{error.GetType().Name}: {error.Message}"));
        }
    }
}
