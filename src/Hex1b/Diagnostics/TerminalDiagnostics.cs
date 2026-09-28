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
        "Graphics (KGP and Sixel placements and animation) are rendered as observed but are not part of the model sequence identity; KGP animation can change a rendering without a model-input event.";
    private const string ConcealedLimitation =
        "Concealed (SGR 8) text is withheld from every format; its cells are returned blank.";
    private const string ObservationalLimitation =
        "Capture issues no repaint, terminal query, or control sequence and sends no input.";
    private const string AnsiRenditionLimitation =
        "ANSI content represents colors and bold, dim, italic, underline, blink, hidden, strikethrough, and overline; reverse video is rendered by swapping colors, and underline color and style variants are not represented.";

    private const string AppFrameNotPublished =
        "Application frames are not yet published with identities through the diagnostics contract.";
    private const string NoApplicationLayer =
        "The workload is not a Hex1b application, so there are no application frames.";
    private const string NativeDeliveryUnavailable =
        "Native delivery outcomes are not observed through the diagnostics contract.";
    private const string NativePresentationUnavailable =
        "What a native terminal host physically displayed is not observable by Hex1b.";

    private static readonly IReadOnlyList<string> CaptureLimitations =
        [ContentMaySecretsLimitation, ImmediateLimitation, CoherenceScopeLimitation, ModelOnlyLimitation, GraphicsLimitation, ConcealedLimitation, ObservationalLimitation];

    private static readonly IReadOnlyList<string> AnsiCaptureLimitations = [.. CaptureLimitations, AnsiRenditionLimitation];

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
    }

    /// <summary>
    /// Immediately captures the terminal model. Never throws for target or request problems;
    /// they are reported through <see cref="DiagnosticCaptureResult.Outcome"/>.
    /// </summary>
    /// <param name="request">What to capture.</param>
    public DiagnosticCaptureResult Capture(DiagnosticCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.Format))
            return Problem(DiagnosticOutcome.InvalidRequest, "unsupported-format", $"Unsupported capture format '{request.Format}'.");
        if (request.HistoryRows < 0)
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-history-rows", "historyRows must be zero or greater.");
        var authorizations = request.Authorizations ?? [];
        foreach (var authorization in authorizations)
        {
            if (!Enum.IsDefined(authorization))
                return Problem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization", $"Unsupported authorization '{authorization}'.");
        }

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
    /// Describes the operations and evidence layers this target supports.
    /// </summary>
    public DiagnosticCapabilities GetCapabilities() => _terminal.IsDisposed
        ? CapabilitiesProblem(DiagnosticOutcome.Unavailable, "target-disposed", "The terminal has been disposed.")
        : new()
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
                Authorizations = [DiagnosticAuthorization.NonScreenMetadata],
                Limitations = AnsiCaptureLimitations,
            },
        ],
        Layers =
        [
            new DiagnosticLayerCapability { Layer = DiagnosticLayer.TerminalModel, Available = true },
            new DiagnosticLayerCapability
            {
                Layer = DiagnosticLayer.ApplicationFrame,
                Reason = _terminal.Workload is Hex1bAppWorkloadAdapter ? AppFrameNotPublished : NoApplicationLayer,
            },
            new DiagnosticLayerCapability { Layer = DiagnosticLayer.NativeDelivery, Reason = NativeDeliveryUnavailable },
            new DiagnosticLayerCapability { Layer = DiagnosticLayer.NativePresentation, Reason = NativePresentationUnavailable },
        ],
    };

    /// <summary>
    /// Human-readable disclosure that a result's content is a partially applied synchronized update,
    /// or <c>null</c> when it is not. Clients show it wherever they present content as text.
    /// </summary>
    internal static string? DescribePartialContent(DiagnosticCaptureResult result) =>
        result.SynchronizedUpdate is { Active: true } update
            ? $"Synchronized update pending since model sequence {update.StartedAtSequence}; the content is partially applied, not a completed frame."
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

        unavailable.Add(new DiagnosticUnavailableField
        {
            Field = "identity.applicationFrame",
            Reason = workload is Hex1bAppWorkloadAdapter ? AppFrameNotPublished : NoApplicationLayer,
        });

        return new DiagnosticObservationIdentity
        {
            ProcessId = Environment.ProcessId,
            ProcessStartedAt = startedAt,
            ModelSequence = state.ModelSequence,
            SessionId = _terminal.DiagnosticSessionId.ToString("N"),
            SourceLayer = DiagnosticLayer.TerminalModel,
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
                ReflowEnabled = state.ReflowEnabled,
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
