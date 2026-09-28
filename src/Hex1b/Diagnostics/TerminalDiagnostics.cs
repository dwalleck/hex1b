using System.Diagnostics;
using System.Reflection;
using Hex1b.Automation;

namespace Hex1b.Diagnostics;

/// <summary>
/// The shared diagnostics engine for one terminal model. In-process targets call it directly;
/// the diagnostics socket exposes the same results to attached CLI and MCP clients, so
/// capability, permission, and outcome policy lives only here.
/// </summary>
/// <remarks>
/// Captures are pulled on request. The engine adds no work to the terminal's output path,
/// and a capture never writes input, repaints, or sends terminal controls.
/// </remarks>
public sealed class TerminalDiagnostics
{
    /// <summary>
    /// Version of the diagnostic request/result contract. Incremented when the meaning of an
    /// existing field changes.
    /// </summary>
    public const int ContractVersion = 1;

    /// <summary>Name of the terminal-model capture operation.</summary>
    public const string CaptureOperation = "capture";

    /// <summary>Timing supported by <see cref="CaptureOperation"/>.</summary>
    public const string ImmediateTiming = "immediate";

    private const string ContentMaySecretsLimitation =
        "Rendered screen and history text can contain secrets. The default policy withholds hidden metadata; it does not make rendered content secret-free.";
    private const string ImmediateLimitation =
        "Immediate observation of the terminal model: a pending synchronized update is reported in synchronizedUpdate and its partially applied content is returned; it is not a completed application frame.";
    private const string CoherenceScopeLimitation =
        "One model read covers the returned cells, rendition, geometry, cursor, modes, retained-history rows, model sequence and synchronized-update state; model activity after that read does not change the result.";
    private const string ModelOnlyLimitation =
        "Terminal-model evidence only: it describes neither the native host's scrollback nor what a native host displayed, and it is not atomic with application, native delivery or host observations; correlate those by identity and acquisition interval.";
    private const string GraphicsLimitation =
        "KGP animation playback advances on a timer without a model event, so SVG and HTML renderings of animated KGP images can differ at the same model sequence; graphics placements change only through output batches and are covered by it.";
    private const string ConcealedLimitation =
        "Concealed (SGR 8) text is withheld from every format; its cells are returned blank.";
    private const string ObservationalLimitation =
        "Capture issues no repaint, terminal query, or control sequence and sends no input.";
    private const string AnsiRenditionLimitation =
        "ANSI content represents colors and bold, dim, italic, underline, blink, hidden, strikethrough, and overline; reverse video is rendered by swapping colors, and underline color and style variants are not represented.";

    /// <summary>Name of the application-frame operation.</summary>
    public const string ApplicationFrameOperation = "application-frame";

    private const string ModelCaptureIsNotAFrame =
        "A terminal-model capture is not an application frame; the application-frame operation returns frames, acquired separately.";
    private const string FramePublicationDisabled =
        "The application does not publish frames; frame publication requires diagnostics to be enabled (WithDiagnostics).";
    private const string NoFocusedEditor = "No editor had focus in this frame.";
    /// <summary>The milestone wait bound applied when a request names none.</summary>
    internal const int DefaultMilestoneTimeoutMs = 5_000;
    private const string WrittenToChild =
        "Written to the child process; its consumption is not observable, so later milestones are unavailable.";
    private const string QueuedForApplication =
        "Queued for the application; input-processed, frame-published and model-applied prove later stages.";
    /// <summary>The longest milestone wait a request may name.</summary>
    internal const int MaxMilestoneTimeoutMs = 60_000;
    private const string MilestoneRequiresAsync =
        "A milestone wait is asynchronous; use CaptureAsync or CaptureApplicationFrameAsync.";
    private const string InputTrackingUnavailable =
        "Input milestones require a Hex1b application with diagnostics enabled (WithDiagnostics); this target does not track input.";
    private const string NoActiveApplication =
        "The terminal hosts Hex1b applications, but none is running (for example a flow between steps, or an application that has not started or has exited).";
    private const string FocusedEditorTextOnly =
        "Only the focused editor's text is included; other editors report metadata only, with their text excluded.";

    private const string FrameIsNotAModelObservation =
        "An application frame is not a terminal-model observation; correlate it with model captures by identities and acquisition intervals.";
    private const string LatestFrameLimitation =
        "The latest frame published by the application loop at the end of a completed pass; an idle application returns an older frame, so compare its projection time with the capture time.";
    private const string FrameLayerLimitation =
        "Application-frame evidence only: it describes the application's own layout, focus and editors, is not atomic with terminal-model captures, and says nothing about native presentation.";
    private const string ApplicationTextLimitation =
        "Rendered application text can contain secrets. Editor text is withheld by default; editor-text includes the focused editor's text only.";
    private const string NoApplicationLayer =
        "The workload is not a Hex1b application, so there are no application frames.";
    private const string NativeDeliveryUnavailable =
        "Native delivery outcomes are not observed through the diagnostics contract.";
    private const string NativePresentationUnavailable =
        "What a native terminal host physically displayed is not observable by Hex1b.";

    private static readonly IReadOnlyList<string> CaptureLimitations =
        [ContentMaySecretsLimitation, ImmediateLimitation, CoherenceScopeLimitation, ModelOnlyLimitation, GraphicsLimitation, ConcealedLimitation, ObservationalLimitation];

    private static readonly IReadOnlyList<string> AnsiCaptureLimitations = [.. CaptureLimitations, AnsiRenditionLimitation];

    private static readonly IReadOnlyList<string> FrameLimitations =
        [LatestFrameLimitation, FrameLayerLimitation, ApplicationTextLimitation, ObservationalLimitation];

    private static readonly string Hex1bVersion = ReadInformationalVersion(typeof(Hex1bTerminal).Assembly)
        ?? typeof(Hex1bTerminal).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static readonly Lazy<(DateTimeOffset? StartedAt, string? Reason)> ProcessStart = new(ReadProcessStart);

    private static readonly Lazy<string?> EntryAssemblyVersion =
        new(() => Assembly.GetEntryAssembly() is { } entry ? ReadInformationalVersion(entry) : null);

    private readonly Hex1bTerminal _terminal;
    private readonly string _applicationName;

    /// <summary>
    /// Runs after the model read and before anything else, so tests can mutate the live model at
    /// that boundary and prove the result describes the read. Production code never sets it.
    /// </summary>
    internal Action? AfterModelReadForTesting { get; init; }

    /// <summary>
    /// Creates the diagnostics engine for a terminal.
    /// </summary>
    /// <param name="terminal">The terminal whose model is observed.</param>
    /// <param name="applicationName">Name reported as the observed application.</param>
    public TerminalDiagnostics(Hex1bTerminal terminal, string? applicationName = null)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _applicationName = applicationName
            ?? Assembly.GetEntryAssembly()?.GetName().Name
            ?? "Hex1bApp";
        _terminal.EnsureAcceptanceTracker();
    }

    /// <summary>
    /// Captures the terminal model like <see cref="Capture"/>, first waiting (bounded) for the
    /// request's milestone when it names one. An unmet milestone returns its outcome with what was
    /// observed and no content.
    /// </summary>
    public async Task<DiagnosticCaptureResult> CaptureAsync(DiagnosticCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Milestone is not { } milestone)
            return Capture(request);
        if (ValidateCapture(request) is { } invalid)
            return invalid;

        var rawInput = request.Authorizations?.Contains(DiagnosticAuthorization.RawInput) == true;
        var wait = await WaitForMilestoneAsync(milestone, rawInput, cancellationToken).ConfigureAwait(false);
        if (wait.Problem is { } problem)
            return Problem(problem.Outcome, problem.Code, problem.Message) with { Milestone = wait.Result };
        var captured = Capture(new DiagnosticCaptureRequest
        {
            Format = request.Format,
            HistoryRows = request.HistoryRows,
            FontFamily = request.FontFamily,
            Authorizations = request.Authorizations,
        });
        return captured.Outcome != DiagnosticOutcome.Captured
            ? captured with { Milestone = wait.Result }
            : captured with
            {
                Milestone = wait.Result,
                ContentCoverage = WithRawInputCoverage(captured.ContentCoverage, wait.Result!, rawInput),
                UnavailableFields = [.. captured.UnavailableFields, .. wait.Unavailable],
            };
    }

    /// <summary>
    /// Returns the latest published application frame like <see cref="CaptureApplicationFrame"/>,
    /// first waiting (bounded) for the request's milestone when it names one.
    /// </summary>
    public async Task<DiagnosticApplicationFrameResult> CaptureApplicationFrameAsync(
        DiagnosticApplicationFrameRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Milestone is not { } milestone)
            return CaptureApplicationFrame(request);
        if (ValidateAuthorizations(request.Authorizations) is { } invalid)
            return invalid;

        var rawInput = request.Authorizations?.Contains(DiagnosticAuthorization.RawInput) == true;
        var wait = await WaitForMilestoneAsync(milestone, rawInput, cancellationToken).ConfigureAwait(false);
        if (wait.Problem is { } problem)
            return FrameProblem(problem.Outcome, problem.Code, problem.Message) with { Milestone = wait.Result };
        var captured = CaptureApplicationFrame(new DiagnosticApplicationFrameRequest { Authorizations = request.Authorizations });
        return captured.Outcome != DiagnosticOutcome.Captured
            ? captured with { Milestone = wait.Result }
            : captured with
            {
                Milestone = wait.Result,
                ContentCoverage = WithRawInputCoverage(captured.ContentCoverage, wait.Result!, rawInput),
                UnavailableFields = [.. captured.UnavailableFields, .. wait.Unavailable],
            };
    }

    // Checked before any milestone wait, so a malformed request fails at once rather than after it.
    private static DiagnosticCaptureResult? ValidateCapture(DiagnosticCaptureRequest request)
    {
        if (!Enum.IsDefined(request.Format))
            return Problem(DiagnosticOutcome.InvalidRequest, "unsupported-format", $"Unsupported capture format '{request.Format}'.");
        if (request.HistoryRows < 0)
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-history-rows", "historyRows must be zero or greater.");
        foreach (var authorization in request.Authorizations ?? [])
        {
            if (!Enum.IsDefined(authorization))
                return Problem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization", $"Unsupported authorization '{authorization}'.");
        }

        return null;
    }

    private static DiagnosticApplicationFrameResult? ValidateAuthorizations(IReadOnlyList<DiagnosticAuthorization>? authorizations)
    {
        foreach (var authorization in authorizations ?? [])
        {
            if (!Enum.IsDefined(authorization))
                return FrameProblem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization", $"Unsupported authorization '{authorization}'.");
        }

        return null;
    }

    // A milestone capture reports the awaited input's payload only under raw-input.
    private static IReadOnlyList<DiagnosticContentCoverage> WithRawInputCoverage(
        IReadOnlyList<DiagnosticContentCoverage> coverage, DiagnosticMilestoneResult milestone, bool rawInput)
    {
        var entry = !rawInput
            ? new DiagnosticContentCoverage
            {
                Content = DiagnosticContentClass.RawInput,
                State = DiagnosticCoverageState.Excluded,
                Reason = "Requires raw-input authorization; milestone results report input metadata only.",
            }
            : milestone.Input?.Payload is not null
                ? new DiagnosticContentCoverage { Content = DiagnosticContentClass.RawInput, State = DiagnosticCoverageState.Included }
                : new DiagnosticContentCoverage
                {
                    Content = DiagnosticContentClass.RawInput,
                    State = DiagnosticCoverageState.Unavailable,
                    Reason = "The awaited input's payload is not retained (evicted, streamed paste content, or an event without a payload).",
                };
        return [.. coverage.Where(c => c.Content != DiagnosticContentClass.RawInput), entry];
    }

    private async Task<(DiagnosticMilestoneResult? Result, (DiagnosticOutcome Outcome, string Code, string Message)? Problem,
        IReadOnlyList<DiagnosticUnavailableField> Unavailable)>
        WaitForMilestoneAsync(DiagnosticMilestoneRequest request, bool rawInput, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Milestone))
            return (null, (DiagnosticOutcome.InvalidRequest, "unsupported-milestone", $"Unsupported milestone '{request.Milestone}'."), []);
        if (request.InputId is not { } inputId)
            return (null, (DiagnosticOutcome.InvalidRequest, "missing-input-id", "A milestone request must name an input id."), []);
        var timeoutMs = request.TimeoutMs ?? DefaultMilestoneTimeoutMs;
        if (timeoutMs is < 1 or > MaxMilestoneTimeoutMs)
            return (null, (DiagnosticOutcome.InvalidRequest, "invalid-milestone-timeout",
                $"A milestone timeout must be 1 to {MaxMilestoneTimeoutMs} ms."), []);
        if (_terminal.IsDisposed)
            return (null, (DiagnosticOutcome.Unavailable, "target-disposed", "The terminal has been disposed."), []);
        if (_terminal.InputMilestones is not { } tracker)
            return (null, (DiagnosticOutcome.Unavailable, "input-tracking-unavailable", InputTrackingUnavailable), []);

        var status = await tracker.WaitAsync(request.Milestone, inputId, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
            .ConfigureAwait(false);
        if (status.Outcome == DiagnosticOutcome.InvalidRequest)
            return (null, (status.Outcome.Value, status.Code!, status.Message!), []);

        var observed = tracker.Observe();
        var record = tracker.Record(inputId);
        var unavailable = new List<DiagnosticUnavailableField>();
        if (record is null)
            unavailable.Add(new DiagnosticUnavailableField
            {
                Field = "milestone.input",
                Reason = $"Only the last {InputMilestoneTracker.RetainedRecords} inputs' records are retained.",
            });
        var result = new DiagnosticMilestoneResult
        {
            Milestone = request.Milestone,
            InputId = inputId,
            Met = status.IsMet,
            AcceptedInput = observed.AcceptedInput,
            ProcessedInput = observed.ProcessedInput,
            ProcessedBy = record?.ProcessedBy,
            Frame = status.Frame is { } frame
                ? new DiagnosticMilestoneFrame
                {
                    ApplicationInstanceId = frame.ApplicationInstanceId,
                    FrameId = frame.FrameId,
                    ProcessedInput = frame.ProcessedInput,
                    WroteOutput = frame.WroteOutput,
                }
                : null,
            // What the model had reached: the proof for model-applied, and the observed progress
            // when any milestone was not met.
            ModelSequence = !status.IsMet || request.Milestone == DiagnosticMilestone.ModelApplied ? _terminal.CurrentModelSequence : null,
            Input = record is null ? null : new DiagnosticInputRecord
            {
                Id = record.Id,
                Kind = record.Kind,
                Source = record.Source,
                AcceptedAt = record.AcceptedAt,
                AcceptedTimestamp = record.AcceptedTimestamp,
                ProcessedAt = record.ProcessedAt,
                ProcessedTimestamp = record.ProcessedTimestamp,
                Payload = rawInput ? record.Payload : null,
            },
        };
        return status.IsMet ? (result, null, unavailable) : (result, (status.Outcome!.Value, status.Code!, status.Message!), unavailable);
    }

    /// <summary>
    /// Runs one diagnostic send and returns the input ids its events were assigned, or
    /// <see langword="null"/> when this target does not track input.
    /// </summary>
    internal Task<DiagnosticAcceptedInput?> TrackSendAsync(Func<Task> send, string kind, CancellationToken cancellationToken = default) =>
        TrackSendAsync(async () =>
        {
            await send().ConfigureAwait(false);
            return true;
        }, kind, cancellationToken);

    /// <summary>
    /// Runs one diagnostic send of <paramref name="kind"/> input (<c>text</c>, <c>key</c> or
    /// <c>mouse</c>) and returns the input ids its events were assigned, or <see langword="null"/>
    /// when this target does not track input or the send reports it delivered nothing.
    /// </summary>
    internal async Task<DiagnosticAcceptedInput?> TrackSendAsync(Func<Task<bool>> send, string kind,
        CancellationToken cancellationToken = default)
    {
        if (_terminal.InputMilestones is not { } tracker)
        {
            await send().ConfigureAwait(false);
            return null;
        }

        // Sends do not nest: waiting for a turn this flow already holds would never end.
        if (tracker.OwnsTurn)
            throw new InvalidOperationException("A diagnostic send cannot start inside another send on the same terminal.");
        await tracker.WaitForSendTurnAsync(cancellationToken).ConfigureAwait(false);
        var scope = tracker.BeginSend();
        long? written = null;
        try
        {
            var delivered = await send().ConfigureAwait(false);
            // A PTY write is numbered inside the turn, so ids follow the order of the writes.
            if (tracker.AcceptanceOnly && delivered)
                written = tracker.AcceptWrite(kind);
        }
        finally
        {
            // Ended before its range is read, so no later write can join a range already reported.
            scope.Dispose();
        }

        if (tracker.AcceptanceOnly)
        {
            return written is { } id
                ? new DiagnosticAcceptedInput { FirstId = id, LastId = id, Meaning = WrittenToChild }
                : null;
        }

        return scope.LastId is { } lastId
            ? new DiagnosticAcceptedInput { FirstId = scope.FirstId!.Value, LastId = lastId, Meaning = QueuedForApplication }
            : null;
    }

    /// <summary>
    /// Immediately captures the terminal model. Never throws for target or request problems;
    /// they are reported through <see cref="DiagnosticCaptureResult.Outcome"/>.
    /// </summary>
    /// <param name="request">What to capture.</param>
    public DiagnosticCaptureResult Capture(DiagnosticCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Milestone is not null)
            return Problem(DiagnosticOutcome.InvalidRequest, "milestone-requires-async", MilestoneRequiresAsync);
        if (ValidateCapture(request) is { } invalid)
            return invalid;

        var authorizations = request.Authorizations ?? [];
        var nonScreen = authorizations.Contains(DiagnosticAuthorization.NonScreenMetadata);
        try
        {
            // The acquisition interval brackets only the model read; rendering happens afterwards.
            var startTimestamp = Stopwatch.GetTimestamp();
            var wallClockStart = DateTimeOffset.UtcNow;
            var state = _terminal.CaptureSnapshotState(request.HistoryRows, ScrollbackWidth.CurrentTerminal);
            var endTimestamp = Stopwatch.GetTimestamp();
            var wallClockEnd = DateTimeOffset.UtcNow;
            if (state.Disposed)
            {
                // Disposal is observed inside the read, so a capture racing disposal can never
                // report the reset model. Release the references the read took.
                new Hex1bTerminalSnapshot(_terminal, state, ScrollbackWidth.CurrentTerminal, TerminalCell.Empty).Dispose();
                return Problem(DiagnosticOutcome.Unavailable, "target-disposed", "The terminal has been disposed.");
            }

            AfterModelReadForTesting?.Invoke();

            var croppedRows = CountCroppedRows(state);
            using var snapshot = new Hex1bTerminalSnapshot(_terminal, ApplyContentPolicy(state, nonScreen),
                ScrollbackWidth.CurrentTerminal, TerminalCell.Empty);
            var content = Render(snapshot, request, nonScreen);

            var unavailable = new List<DiagnosticUnavailableField>();
            var history = DescribeHistory(request.HistoryRows, state, snapshot.ScrollbackLineCount, croppedRows, unavailable);
            var identity = DescribeIdentity(state, new DiagnosticAcquisition
            {
                ClockDomain = "process-monotonic",
                Frequency = Stopwatch.Frequency,
                StartTimestamp = startTimestamp,
                EndTimestamp = endTimestamp,
                WallClockStart = wallClockStart,
                WallClockEnd = wallClockEnd,
            }, unavailable);

            return new DiagnosticCaptureResult
            {
                Outcome = DiagnosticOutcome.Captured,
                Format = request.Format,
                Content = content,
                Geometry = new DiagnosticGeometry
                {
                    Columns = state.TerminalWidth,
                    Rows = state.TerminalHeight,
                    AlternateScreen = state.InAlternateScreen,
                    CursorColumn = state.CursorX,
                    CursorRow = state.CursorY,
                },
                SynchronizedUpdate = new DiagnosticSynchronizedUpdate
                {
                    Active = state.SynchronizedUpdatePending,
                    StartedAtSequence = state.SynchronizedUpdatePending ? state.SynchronizedUpdateStartedSequence : null,
                },
                History = history,
                Identity = identity,
                ContentCoverage = DescribeCoverage(request, authorizations, history),
                NonScreenMetadata = nonScreen
                    ? new DiagnosticNonScreenMetadata { WindowTitle = state.WindowTitle, IconName = state.IconName }
                    : null,
                UnavailableFields = unavailable,
                Limitations = request.Format == DiagnosticCaptureFormat.Ansi ? AnsiCaptureLimitations : CaptureLimitations,
            };
        }
        catch (ObjectDisposedException)
        {
            return Problem(DiagnosticOutcome.Unavailable, "target-disposed", "The terminal was disposed during capture.");
        }
        catch (Exception error)
        {
            // Keep the failure diagnosable: the result carries the exception type, and the full
            // exception goes to trace listeners.
            Trace.TraceError($"Hex1b diagnostic capture failed: {error}");
            return Problem(DiagnosticOutcome.Failed, "capture-failed", $"{error.GetType().FullName}: {error.Message}");
        }
    }

    /// <summary>
    /// Returns the latest application frame published by the target's Hex1b application loop.
    /// Never waits for, requests, or forces a frame. Target and request problems are reported
    /// through <see cref="DiagnosticApplicationFrameResult.Outcome"/>.
    /// </summary>
    /// <param name="request">Authorizations for the capture.</param>
    public DiagnosticApplicationFrameResult CaptureApplicationFrame(DiagnosticApplicationFrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Milestone is not null)
            return FrameProblem(DiagnosticOutcome.InvalidRequest, "milestone-requires-async", MilestoneRequiresAsync);
        if (ValidateAuthorizations(request.Authorizations) is { } invalid)
            return invalid;
        var authorizations = request.Authorizations ?? [];

        if (_terminal.IsDisposed)
            return FrameProblem(DiagnosticOutcome.Unavailable, "target-disposed", "The terminal has been disposed.");
        var (source, code, reason) = ReadFrameLayer();
        if (source is null)
            return FrameProblem(DiagnosticOutcome.Unavailable, code!, reason!);
        if (source.LatestFrame is not { } published)
            return FrameProblem(DiagnosticOutcome.Unavailable, "no-application-frame-yet",
                "The application has not completed a render pass since frame publication started.");
        if (published.Frame is not { } frame)
            return FrameProblem(DiagnosticOutcome.Failed, "application-frame-projection-failed",
                $"Projecting application frame {published.FrameId} failed: {published.Failure}");

        var unavailable = new List<DiagnosticUnavailableField>
        {
            new() { Field = "identity.modelSequence", Reason = FrameIsNotAModelObservation },
        };
        if (frame.Root is null)
            unavailable.Add(new DiagnosticUnavailableField { Field = "frame.root", Reason = "The application had no nodes in this frame." });
        if (frame.FocusedEditor is null)
            unavailable.Add(new DiagnosticUnavailableField { Field = "frame.focusedEditor", Reason = NoFocusedEditor });
        if (frame.Timings is null)
            unavailable.Add(new DiagnosticUnavailableField
            {
                Field = "frame.timings",
                Reason = "Diagnostic timing was not enabled for this application, so frame and node timings were not recorded.",
            });

        // The published frame keeps the focused editor's text; only an editor-text capture sees it.
        var editorTextAuthorized = authorizations.Contains(DiagnosticAuthorization.EditorText);
        if (!editorTextAuthorized && frame.FocusedEditor is { Text: not null } editor)
            frame = frame with { FocusedEditor = editor with { Text = null } };
        var editorTextCoverage = (editorTextAuthorized, frame.FocusedEditor) switch
        {
            (false, _) => new DiagnosticContentCoverage
            {
                Content = DiagnosticContentClass.EditorText,
                State = DiagnosticCoverageState.Excluded,
                Reason = "Requires editor-text authorization.",
            },
            (true, null) => new DiagnosticContentCoverage
            {
                Content = DiagnosticContentClass.EditorText,
                State = DiagnosticCoverageState.Unavailable,
                Reason = NoFocusedEditor,
            },
            (true, _) => new DiagnosticContentCoverage
            {
                Content = DiagnosticContentClass.EditorText,
                State = DiagnosticCoverageState.Included,
                Reason = FocusedEditorTextOnly,
            },
        };

        var identity = DescribeIdentity(DiagnosticLayer.ApplicationFrame, modelSequence: null, published.FrameId,
            frame.ApplicationInstanceId,
            _terminal.PresentationAdapter is Reflow.ITerminalReflowProvider { ReflowEnabled: true },
            new DiagnosticAcquisition
            {
                ClockDomain = "process-monotonic",
                Frequency = Stopwatch.Frequency,
                StartTimestamp = published.ProjectionStartTimestamp,
                EndTimestamp = published.ProjectionEndTimestamp,
                WallClockStart = published.ProjectionStart,
                WallClockEnd = published.ProjectionEnd,
            }, unavailable);

        return new DiagnosticApplicationFrameResult
        {
            Outcome = DiagnosticOutcome.Captured,
            Frame = frame,
            Identity = identity,
            ContentCoverage =
            [
                new DiagnosticContentCoverage { Content = DiagnosticContentClass.ApplicationText, State = DiagnosticCoverageState.Included },
                editorTextCoverage,
            ],
            UnavailableFields = unavailable,
            Limitations = FrameLimitations,
        };
    }

    /// <summary>
    /// Creates an application-frame result for an operation that produced no frame.
    /// </summary>
    internal static DiagnosticApplicationFrameResult FrameProblem(DiagnosticOutcome outcome, string code, string message) => new()
    {
        Outcome = outcome,
        Problem = new DiagnosticProblem { Code = code, Message = message },
        UnavailableFields = [new DiagnosticUnavailableField { Field = "identity", Reason = "No frame was returned." }],
    };

    private static IReadOnlyList<DiagnosticMilestoneCapability> DescribeMilestones(InputMilestoneTracker? tracker) =>
        MilestoneGuarantees.Select(entry => new DiagnosticMilestoneCapability
        {
            Milestone = entry.Key,
            Guarantee = entry.Value,
            Available = tracker is not null && (!tracker.AcceptanceOnly || entry.Key == DiagnosticMilestone.InputAccepted),
            Reason = tracker is null ? InputTrackingUnavailable
                : tracker.AcceptanceOnly && entry.Key != DiagnosticMilestone.InputAccepted
                    ? "Input is written to a child process whose consumption is not observable."
                    : null,
        }).ToArray();

    private static readonly IReadOnlyDictionary<DiagnosticMilestone, string> MilestoneGuarantees = new Dictionary<DiagnosticMilestone, string>
    {
        [DiagnosticMilestone.InputAccepted] =
            "The input was queued for the application (for a PTY: written to the child process). It does not prove processing.",
        [DiagnosticMilestone.InputProcessed] =
            "The application loop consumed the input (its processed-input watermark reached the id, or the flow runner consumed it).",
        [DiagnosticMilestone.FramePublished] =
            "A frame whose processed-input watermark covers the input was published; frames published before the input was processed are never attributed to it.",
        [DiagnosticMilestone.ModelApplied] =
            "Everything the application enqueued up to that frame was applied to the terminal model. Not a native delivery or presentation acknowledgment; unavailable for inline flow steps.",
    };

    // Reads the frame source once: a flow swaps it as steps start and finish.
    private (IApplicationFrameSource? Source, string? Code, string? Reason) ReadFrameLayer()
    {
        if (_terminal.Workload is not Hex1bAppWorkloadAdapter adapter)
            return (null, "no-application-layer", NoApplicationLayer);
        if (adapter.ApplicationFrameSource is not { } source)
        {
            if (!adapter.HostsApplications)
                return (null, "no-application-layer", NoApplicationLayer);
            return adapter.DiagnosticTimingEnabled
                ? (null, "no-active-application", NoActiveApplication)
                : (null, "application-frame-publication-disabled", FramePublicationDisabled);
        }

        return source.FramePublicationEnabled
            ? (source, null, null)
            : (null, "application-frame-publication-disabled", FramePublicationDisabled);
    }

    /// <summary>
    /// Describes the operations and evidence layers this target supports.
    /// </summary>
    public DiagnosticCapabilities GetCapabilities()
    {
        if (_terminal.IsDisposed)
            return CapabilitiesProblem(DiagnosticOutcome.Unavailable, "target-disposed", "The terminal has been disposed.");

        var frameLayer = ReadFrameLayer();
        var milestones = DescribeMilestones(_terminal.InputMilestones);
        return new()
        {
            Outcome = DiagnosticOutcome.Captured,
            Operations =
            [
                new DiagnosticOperationCapability
                {
                    Operation = CaptureOperation,
                    Layer = DiagnosticLayer.TerminalModel,
                    Formats = Enum.GetValues<DiagnosticCaptureFormat>(),
                    Timing = [ImmediateTiming],
                    ModelHistory = true,
                    Authorizations = [DiagnosticAuthorization.NonScreenMetadata, DiagnosticAuthorization.RawInput],
                    Limitations = AnsiCaptureLimitations,
                    Milestones = milestones,
                },
                new DiagnosticOperationCapability
                {
                    Operation = ApplicationFrameOperation,
                    Layer = DiagnosticLayer.ApplicationFrame,
                    Timing = ["latest-published"],
                    Authorizations = [DiagnosticAuthorization.EditorText, DiagnosticAuthorization.RawInput],
                    Limitations = FrameLimitations,
                    Milestones = milestones,
                },
            ],
            Layers =
            [
                new DiagnosticLayerCapability { Layer = DiagnosticLayer.TerminalModel, Available = true },
                new DiagnosticLayerCapability
                {
                    Layer = DiagnosticLayer.ApplicationFrame,
                    Available = frameLayer.Code is null or "no-active-application",
                    Reason = frameLayer.Reason,
                },
                new DiagnosticLayerCapability { Layer = DiagnosticLayer.NativeDelivery, Reason = NativeDeliveryUnavailable },
                new DiagnosticLayerCapability { Layer = DiagnosticLayer.NativePresentation, Reason = NativePresentationUnavailable },
            ],
        };
    }

    /// <summary>
    /// Human-readable disclosure that a result's content is a partially applied synchronized update,
    /// or <c>null</c> when it is not. Clients show it wherever they present content as text.
    /// </summary>
    internal static string? DescribePartialContent(DiagnosticCaptureResult result) =>
        result.SynchronizedUpdate is { Active: true } update
            ? (update.StartedAtSequence is { } start ? $"Synchronized update pending since model sequence {start}" : "Synchronized update pending") +
              "; the content is partially applied, not a completed frame."
            : null;

    /// <summary>
    /// Creates a result for an operation that produced no observation.
    /// </summary>
    internal static DiagnosticCaptureResult Problem(DiagnosticOutcome outcome, string code, string message) => new()
    {
        Outcome = outcome,
        Problem = new DiagnosticProblem { Code = code, Message = message },
        UnavailableFields = [new DiagnosticUnavailableField { Field = "identity", Reason = "No observation was acquired." }],
    };

    /// <summary>
    /// Creates a capability description for a target that could not be described.
    /// </summary>
    internal static DiagnosticCapabilities CapabilitiesProblem(DiagnosticOutcome outcome, string code, string message) => new()
    {
        Outcome = outcome,
        Problem = new DiagnosticProblem { Code = code, Message = message },
    };

    // Applies the content policy to the snapshot's private cell copies once, so every renderer
    // sees only permitted content. Scrollback rows share cell arrays with the live model, so
    // they are copied before redaction. Hyperlink references the snapshot will no longer hold
    // are released here to balance the references taken when the state was captured.
    private static Hex1bTerminalSnapshotState ApplyContentPolicy(Hex1bTerminalSnapshotState state, bool keepHyperlinks)
    {
        var screen = state.ScreenBuffer;
        for (var y = 0; y < state.TerminalHeight; y++)
            for (var x = 0; x < state.TerminalWidth; x++)
                screen[y, x] = RedactCell(screen[y, x], keepHyperlinks);

        var rows = new ScrollbackRow[state.ScrollbackRows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = state.ScrollbackRows[i];
            var cells = new TerminalCell[Math.Min(row.Cells.Length, state.TerminalWidth)];
            for (var x = 0; x < cells.Length; x++)
                cells[x] = RedactCell(row.Cells[x], keepHyperlinks);
            rows[i] = row with { Cells = cells };
        }

        return state with
        {
            ScreenBuffer = screen,
            ScrollbackRows = rows,
            ActiveHyperlink = keepHyperlinks ? state.ActiveHyperlink : null,
        };
    }

    private static TerminalCell RedactCell(TerminalCell cell, bool keepHyperlinks)
    {
        if (!keepHyperlinks && cell.TrackedHyperlink is { } hyperlink)
        {
            hyperlink.Release();
            cell = cell with { TrackedHyperlink = null };
        }

        if ((cell.Attributes & CellAttributes.Hidden) != 0 && !string.IsNullOrEmpty(cell.Character) && cell.Character != " ")
            cell = cell with { Character = new string(' ', Math.Max(1, DisplayWidth.GetGraphemeWidth(cell.Character))) };

        return cell;
    }

    // Returned history rows that lose non-blank retained content when cropped to the screen width.
    private static int CountCroppedRows(Hex1bTerminalSnapshotState state)
    {
        var cropped = 0;
        foreach (var row in state.ScrollbackRows)
        {
            for (var x = state.TerminalWidth; x < row.Cells.Length; x++)
            {
                var character = row.Cells[x].Character;
                if (!string.IsNullOrEmpty(character) && character != " " && character != "\0" && character != "\uE000")
                {
                    cropped++;
                    break;
                }
            }
        }

        return cropped;
    }

    private static string Render(Hex1bTerminalSnapshot snapshot, DiagnosticCaptureRequest request, bool nonScreen)
    {
        switch (request.Format)
        {
            case DiagnosticCaptureFormat.Text:
                return snapshot.GetText();
            case DiagnosticCaptureFormat.Ansi:
                var ansiOptions = new TerminalAnsiOptions { IncludeClearScreen = true, IncludeTrailingNewline = true };
                return nonScreen
                    ? snapshot.ToAnsi(ansiOptions, includeHyperlinks: true)
                    : snapshot.ToAnsi(ansiOptions);
            default:
                var options = new TerminalSvgOptions { ShowCellGrid = false };
                if (request.FontFamily is { } fontFamily)
                    options.FontFamily = $"'{fontFamily}'";
                return request.Format == DiagnosticCaptureFormat.Svg ? snapshot.ToSvg(options) : snapshot.ToHtml(options);
        }
    }

    private DiagnosticHistoryCoverage DescribeHistory(int requested, Hex1bTerminalSnapshotState state, int returned,
        int croppedRows, List<DiagnosticUnavailableField> unavailable)
    {
        var capacity = _terminal.HistoryRetentionCapacity;
        string? reason = null;
        int? available = state.RetainedHistoryRows;
        if (capacity == 0)
        {
            reason = "Model history retention is not configured for this terminal.";
            available = null;
        }
        else if (state.InAlternateScreen)
        {
            reason = "The alternate screen is active; retained primary-screen history is not part of this observation.";
            available = null;
        }
        else if (croppedRows > 0)
        {
            reason = $"{croppedRows} returned row(s) held content wider than the current {state.TerminalWidth} columns and were cropped.";
        }

        if (available is null)
            unavailable.Add(new DiagnosticUnavailableField { Field = "history.availableRows", Reason = reason! });

        return new DiagnosticHistoryCoverage
        {
            RequestedRows = requested,
            AvailableRows = available,
            ReturnedRows = returned,
            CroppedRows = croppedRows,
            Truncated = croppedRows > 0 || (available is int count && returned < Math.Min(requested, count)),
            RetentionCapacity = capacity,
            Reason = reason,
        };
    }

    private DiagnosticObservationIdentity DescribeIdentity(
        Hex1bTerminalSnapshotState state, DiagnosticAcquisition acquisition, List<DiagnosticUnavailableField> unavailable)
    {
        var notAFrame = _terminal.Workload is Hex1bAppWorkloadAdapter ? ModelCaptureIsNotAFrame : NoApplicationLayer;
        unavailable.Add(new DiagnosticUnavailableField { Field = "identity.applicationFrame", Reason = notAFrame });
        unavailable.Add(new DiagnosticUnavailableField { Field = "identity.applicationInstanceId", Reason = notAFrame });
        return DescribeIdentity(DiagnosticLayer.TerminalModel, state.ModelSequence, applicationFrame: null,
            applicationInstanceId: null, state.ReflowEnabled, acquisition, unavailable);
    }

    private DiagnosticObservationIdentity DescribeIdentity(DiagnosticLayer layer, long? modelSequence, long? applicationFrame,
        string? applicationInstanceId, bool reflowEnabled, DiagnosticAcquisition acquisition, List<DiagnosticUnavailableField> unavailable)
    {
        var (startedAt, startReason) = ProcessStart.Value;
        if (startedAt is null)
            unavailable.Add(new DiagnosticUnavailableField { Field = "identity.processStartedAt", Reason = startReason! });

        var workload = _terminal.Workload;
        string? applicationVersion = null;
        if (workload is Hex1bAppWorkloadAdapter)
        {
            applicationVersion = EntryAssemblyVersion.Value;
            if (applicationVersion is null)
                unavailable.Add(new DiagnosticUnavailableField
                {
                    Field = "identity.applicationVersion",
                    Reason = "The process entry assembly reports no informational version.",
                });
        }
        else
        {
            unavailable.Add(new DiagnosticUnavailableField
            {
                Field = "identity.applicationVersion",
                Reason = "The workload is not an in-process Hex1b application and reports no version to the terminal model.",
            });
        }

        return new DiagnosticObservationIdentity
        {
            ProcessId = Environment.ProcessId,
            ProcessStartedAt = startedAt,
            ModelSequence = modelSequence,
            ApplicationFrame = applicationFrame,
            ApplicationInstanceId = applicationInstanceId,
            SessionId = _terminal.DiagnosticSessionId.ToString("N"),
            SourceLayer = layer,
            ApplicationName = _applicationName,
            ApplicationVersion = applicationVersion,
            Hex1bVersion = Hex1bVersion,
            Configuration = new DiagnosticModelConfiguration
            {
                Workload = workload switch
                {
                    Hex1bAppWorkloadAdapter => "hex1b-application",
                    Hex1bTerminalChildProcess => "pty-process",
                    StandardProcessWorkloadAdapter => "process",
                    _ => workload.GetType().Name,
                },
                Presentation = _terminal.PresentationAdapter is HeadlessPresentationAdapter
                    ? "headless"
                    : _terminal.PresentationAdapter.GetType().Name,
                ReflowEnabled = reflowEnabled,
                HistoryRetentionCapacity = _terminal.HistoryRetentionCapacity,
            },
            Acquisition = acquisition,
        };
    }

    private static IReadOnlyList<DiagnosticContentCoverage> DescribeCoverage(
        DiagnosticCaptureRequest request, IReadOnlyList<DiagnosticAuthorization> authorizations, DiagnosticHistoryCoverage history)
    {
        const string nonScreenRequired = "Requires non-screen-metadata authorization.";
        var nonScreen = authorizations.Contains(DiagnosticAuthorization.NonScreenMetadata);
        var hyperlinkFormat = request.Format is DiagnosticCaptureFormat.Ansi or DiagnosticCaptureFormat.Html;

        return
        [
            Included(DiagnosticContentClass.RenderedScreen),
            Coverage(DiagnosticContentClass.ConcealedText, DiagnosticCoverageState.Excluded,
                "Concealed (SGR 8) text is not rendered; captures return its cells blank."),
            request.HistoryRows == 0
                ? Coverage(DiagnosticContentClass.RenderedHistory, DiagnosticCoverageState.Excluded, "No history rows were requested.")
                : history.AvailableRows is null
                    ? Coverage(DiagnosticContentClass.RenderedHistory, DiagnosticCoverageState.Unavailable, history.Reason!)
                    : Included(DiagnosticContentClass.RenderedHistory),
            !nonScreen
                ? Coverage(DiagnosticContentClass.HyperlinkTargets, DiagnosticCoverageState.Excluded, nonScreenRequired)
                : hyperlinkFormat
                    ? Included(DiagnosticContentClass.HyperlinkTargets)
                    : Coverage(DiagnosticContentClass.HyperlinkTargets, DiagnosticCoverageState.Unavailable,
                        $"The {request.Format.ToString().ToLowerInvariant()} format does not represent hyperlink targets."),
            nonScreen
                ? Included(DiagnosticContentClass.WindowTitle)
                : Coverage(DiagnosticContentClass.WindowTitle, DiagnosticCoverageState.Excluded, nonScreenRequired),
            authorizations.Contains(DiagnosticAuthorization.EditorText)
                ? Coverage(DiagnosticContentClass.EditorText, DiagnosticCoverageState.Unavailable,
                    "Terminal-model capture does not collect application editor text.")
                : Coverage(DiagnosticContentClass.EditorText, DiagnosticCoverageState.Excluded, "Requires editor-text authorization."),
            authorizations.Contains(DiagnosticAuthorization.RawInput)
                ? Coverage(DiagnosticContentClass.RawInput, DiagnosticCoverageState.Unavailable,
                    "Terminal-model capture does not collect keyboard input.")
                : Coverage(DiagnosticContentClass.RawInput, DiagnosticCoverageState.Excluded, "Requires raw-input authorization."),
        ];

        static DiagnosticContentCoverage Included(DiagnosticContentClass content) =>
            new() { Content = content, State = DiagnosticCoverageState.Included };

        static DiagnosticContentCoverage Coverage(DiagnosticContentClass content, DiagnosticCoverageState state, string reason) =>
            new() { Content = content, State = state, Reason = reason };
    }

    private static string? ReadInformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static (DateTimeOffset?, string?) ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return (new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero), null);
        }
        catch (Exception error) when (error is PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, $"The platform did not report the process start time: {error.Message}");
        }
    }
}
