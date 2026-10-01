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
            new DiagnosticsRequest { Method = TerminalDiagnostics.CaptureOperation, Capture = request }, cancellationToken,
            MilestoneWait(request.Milestone)).ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.Problem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.Capture is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return TerminalDiagnostics.Problem(outcome, code, message);
        }

        if (request.Milestone is not null && result.Outcome == DiagnosticOutcome.Captured && result.Milestone is null)
            return TerminalDiagnostics.Problem(DiagnosticOutcome.Failed, "incompatible-target", MilestoneIgnored);

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
            cancellationToken, MilestoneWait(request.Milestone)).ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.FrameProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.ApplicationFrame is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return TerminalDiagnostics.FrameProblem(outcome, code, message);
        }

        if (request.Milestone is not null && result.Outcome == DiagnosticOutcome.Captured && result.Milestone is null)
            return TerminalDiagnostics.FrameProblem(DiagnosticOutcome.Failed, "incompatible-target", MilestoneIgnored);

        return Validate(result);
    }

    /// <summary>
    /// Returns an attached target's native delivery record. Transport, protocol, and contract failures
    /// become diagnostic outcomes; only caller cancellation throws.
    /// </summary>
    public async Task<DiagnosticDeliveryResult> CaptureDeliveryAsync(
        string socketPath, DiagnosticDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = TerminalDiagnostics.DeliveryOperation, Delivery = request },
            cancellationToken).ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.DeliveryProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.Delivery is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return TerminalDiagnostics.DeliveryProblem(outcome, code, message);
        }

        return Validate(result);
    }

    /// <summary>
    /// Starts a bounded diagnostic case on an attached target; the target validates the request and writes
    /// the artifact on its own filesystem. The exchange is not timed out: a start the target is still
    /// applying is never reported failed. Transport, protocol, and contract failures become diagnostic
    /// outcomes; only caller cancellation throws.
    /// </summary>
    public Task<DiagnosticCaseResult> StartCaseAsync(string socketPath, DiagnosticCaseStartRequest request,
        CancellationToken cancellationToken = default) =>
        CaseExchangeAsync(socketPath, new DiagnosticsRequest { Method = TerminalDiagnostics.CaseStartOperation, CaseStart = request },
            untimed: true, cancellationToken);

    /// <summary>
    /// Stops an attached target's case, returning once its artifact is finished or its drain bound passed.
    /// Not timed out, like <see cref="StartCaseAsync"/>.
    /// </summary>
    public Task<DiagnosticCaseResult> StopCaseAsync(string socketPath, CancellationToken cancellationToken = default) =>
        CaseExchangeAsync(socketPath, new DiagnosticsRequest { Method = TerminalDiagnostics.CaseStopOperation }, untimed: true, cancellationToken);

    /// <summary>Reports an attached target's active case.</summary>
    public Task<DiagnosticCaseResult> GetCaseStatusAsync(string socketPath, CancellationToken cancellationToken = default) =>
        CaseExchangeAsync(socketPath, new DiagnosticsRequest { Method = TerminalDiagnostics.CaseStatusOperation }, untimed: false, cancellationToken);

    /// <summary>
    /// Marks a boundary in an attached target's active case. Untimed like a start: a mark the target took is
    /// never reported failed.
    /// </summary>
    public async Task<DiagnosticCaseMarkResult> MarkCaseAsync(string socketPath, string? label = null, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = TerminalDiagnostics.CaseMarkOperation, CaseMarkLabel = label }, cancellationToken, untimed: true).ConfigureAwait(false);
        if (problem is not null)
            return MarkProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);
        if (response!.CaseMark is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return MarkProblem(outcome, code, message);
        }

        if (result.ContractVersion != TerminalDiagnostics.ContractVersion)
            return MarkProblem(DiagnosticOutcome.Failed, "incompatible-target", VersionMessage(result.ContractVersion));
        if (result.Outcome == DiagnosticOutcome.Captured
            && (result.CaseId is null || result.Label is null || result.CheckpointOrdinal is null || result.ModelSequence is null || result.StateRecorded is null))
            return MarkProblem(DiagnosticOutcome.Failed, "protocol-error", "The target reported a mark without its case, label, ordinal, model sequence or state flag.");
        if (result.Outcome != DiagnosticOutcome.Captured && result.Problem is null)
            return MarkProblem(DiagnosticOutcome.Failed, "protocol-error",
                $"The target reported outcome '{DiagnosticContractNames.Of(result.Outcome)}' without a problem.");
        return result;
    }

    private static DiagnosticCaseMarkResult MarkProblem(DiagnosticOutcome outcome, string code, string message) => new()
    {
        Outcome = outcome,
        Problem = new DiagnosticProblem { Code = code, Message = message },
    };

    /// <summary>
    /// Takes a recovery checkpoint in an attached target's active case: a new origin to re-apply from after recording
    /// loss. Untimed like a mark: a recovery the target took is never reported failed.
    /// </summary>
    public async Task<DiagnosticCaseRecoverResult> RecoverCaseAsync(string socketPath, string? label = null, CancellationToken cancellationToken = default)
    {
        var (response, problem) = await ExchangeAsync(socketPath,
            new DiagnosticsRequest { Method = TerminalDiagnostics.CaseRecoverOperation, CaseRecoverLabel = label }, cancellationToken, untimed: true).ConfigureAwait(false);
        if (problem is not null)
            return RecoverProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);
        if (response!.CaseRecover is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return RecoverProblem(outcome, code, message);
        }

        if (result.ContractVersion != TerminalDiagnostics.ContractVersion)
            return RecoverProblem(DiagnosticOutcome.Failed, "incompatible-target", VersionMessage(result.ContractVersion));
        if (result.Outcome == DiagnosticOutcome.Captured
            && (result.CaseId is null || result.Label is null || result.CheckpointOrdinal is null || result.ModelSequence is null || result.Status is null))
            return RecoverProblem(DiagnosticOutcome.Failed, "protocol-error", "The target reported a recovery without its case, label, ordinal, model sequence or status.");
        if (result.Outcome != DiagnosticOutcome.Captured && result.Problem is null)
            return RecoverProblem(DiagnosticOutcome.Failed, "protocol-error",
                $"The target reported outcome '{DiagnosticContractNames.Of(result.Outcome)}' without a problem.");
        return result;
    }

    private static DiagnosticCaseRecoverResult RecoverProblem(DiagnosticOutcome outcome, string code, string message) => new()
    {
        Outcome = outcome,
        Problem = new DiagnosticProblem { Code = code, Message = message },
    };

    private async Task<DiagnosticCaseResult> CaseExchangeAsync(string socketPath, DiagnosticsRequest request, bool untimed,
        CancellationToken cancellationToken)
    {
        var (response, problem) = await ExchangeAsync(socketPath, request, cancellationToken, untimed: untimed).ConfigureAwait(false);
        if (problem is not null)
            return TerminalDiagnostics.CaseProblem(problem.Value.Outcome, problem.Value.Code, problem.Value.Message);

        if (response!.Case is not { } result)
        {
            var (outcome, code, message) = Unexpected(response);
            return TerminalDiagnostics.CaseProblem(outcome, code, message);
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

    private static DiagnosticCaseResult Validate(DiagnosticCaseResult result)
    {
        result = result with
        {
            Authorizations = result.Authorizations ?? [],
            Streams = (result.Streams ?? []).Where(entry => entry is not null).ToArray(),
        };

        if (result.ContractVersion != TerminalDiagnostics.ContractVersion)
            return TerminalDiagnostics.CaseProblem(DiagnosticOutcome.Failed, "incompatible-target", VersionMessage(result.ContractVersion));

        if (result.Outcome == DiagnosticOutcome.Captured
            && (result.CaseId is null || result.Path is null || result.State is null || result.Bounds is null || result.Checkpoint is null))
        {
            return TerminalDiagnostics.CaseProblem(DiagnosticOutcome.Failed, "protocol-error",
                "The target reported a case without its id, path, state, bounds or checkpoint.");
        }

        if (result.Outcome != DiagnosticOutcome.Captured && result.Problem is null)
        {
            return TerminalDiagnostics.CaseProblem(DiagnosticOutcome.Failed, "protocol-error",
                $"The target reported outcome '{DiagnosticContractNames.Of(result.Outcome)}' without a problem.");
        }

        return result;
    }

    private static DiagnosticDeliveryResult Validate(DiagnosticDeliveryResult result)
    {
        result = result with
        {
            Records = result.Records ?? [],
            ContentCoverage = (result.ContentCoverage ?? []).Where(entry => entry is not null).ToArray(),
            UnavailableFields = (result.UnavailableFields ?? []).Where(entry => entry is not null).ToArray(),
            Limitations = (result.Limitations ?? []).Where(entry => entry is not null).ToArray(),
        };

        if (result.ContractVersion != TerminalDiagnostics.ContractVersion)
            return TerminalDiagnostics.DeliveryProblem(DiagnosticOutcome.Failed, "incompatible-target", VersionMessage(result.ContractVersion));

        if (result.Outcome == DiagnosticOutcome.Captured
            && (result.Totals is null || result.Identity is null || result.DeliveryLayer is null || result.Records.Any(r => r is null)))
        {
            return TerminalDiagnostics.DeliveryProblem(DiagnosticOutcome.Failed, "protocol-error",
                "The target reported a delivery record without its totals, identity, layer, or with a missing record.");
        }

        if (result.Outcome != DiagnosticOutcome.Captured && result.Records.Count > 0)
        {
            return TerminalDiagnostics.DeliveryProblem(DiagnosticOutcome.Failed, "protocol-error",
                $"The target reported outcome '{DiagnosticContractNames.Of(result.Outcome)}' with delivery records.");
        }

        if (result.Outcome != DiagnosticOutcome.Captured && result.Problem is null)
        {
            return TerminalDiagnostics.DeliveryProblem(DiagnosticOutcome.Failed, "protocol-error",
                $"The target reported outcome '{DiagnosticContractNames.Of(result.Outcome)}' without a problem.");
        }

        return result;
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

        // Only a captured result carries a frame; a stray one on another outcome is not surfaced.
        if (result.Outcome != DiagnosticOutcome.Captured)
            return result.Frame is null ? result : result with { Frame = null };

        if (!IsComplete(result.Frame!))
        {
            return TerminalDiagnostics.FrameProblem(DiagnosticOutcome.Failed, "protocol-error",
                "The target reported an application frame with missing lists, entries, or positions.");
        }

        return result;
    }

    // Deserialization leaves omitted lists null despite their initializers, and a malformed peer
    // can send null entries; a frame the contract promises complete is checked before callers walk it.
    private static bool IsComplete(DiagnosticApplicationFrame frame)
    {
        static bool Caret(DiagnosticCaret? caret) => caret is not null;

        static bool EditorComplete(DiagnosticEditorState? editor) =>
            editor is null
            || (editor.Bounds is not null && editor.Carets is not null && editor.Carets.All(Caret)
                && editor.Selections is not null && editor.Selections.All(s => s is not null && Caret(s.Start) && Caret(s.End)));

        static bool NodeComplete(DiagnosticFrameNode? node) =>
            node is not null && node.Bounds is not null && node.HitTestBounds is not null && node.ContentBounds is not null
            && node.VisibleBounds is not null && EditorComplete(node.Editor)
            && node.Children is not null && node.Children.All(NodeComplete);

        return frame.Popups is not null && frame.Popups.All(p => p is not null)
            && frame.Focus?.Focusables is not null && frame.Focus.Focusables.All(f => f is { Bounds: not null, HitTestBounds: not null })
            && EditorComplete(frame.FocusedEditor)
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

    // A target predating milestones ignores the request's milestone and captures at once; that
    // capture proves nothing about the awaited input, so it is not reported as captured.
    private const string MilestoneIgnored =
        "The target returned a capture without the requested milestone result; it does not support input milestones.";

    // A milestone capture may legitimately wait its whole timeout before the target answers.
    private static TimeSpan MilestoneWait(DiagnosticMilestoneRequest? milestone) =>
        milestone is null
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Max(0, milestone.TimeoutMs ?? TerminalDiagnostics.DefaultMilestoneTimeoutMs));

    private async Task<(DiagnosticsResponse? Response, (DiagnosticOutcome Outcome, string Code, string Message)? Problem)> ExchangeAsync(
        string socketPath, DiagnosticsRequest request, CancellationToken cancellationToken, TimeSpan extraWait = default, bool untimed = false)
    {
        try
        {
            return (await SendAsync(socketPath, request, cancellationToken, untimed ? null : _observationTimeout + extraWait).ConfigureAwait(false), null);
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
