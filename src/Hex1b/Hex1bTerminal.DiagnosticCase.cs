using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // At most one diagnostic case records this terminal. It is armed and cleared under the model lock,
    // so its freshness check and its first event are ordered with every model mutation.
    private DiagnosticCaseRecorder? _diagnosticCase;

    // The configuration that determines this model's fresh state, captured at construction.
    private DiagnosticCaseModelConfiguration _caseConfiguration = new();

    /// <summary>The active diagnostic case, if any.</summary>
    internal DiagnosticCaseRecorder? DiagnosticCase => Volatile.Read(ref _diagnosticCase);

    /// <summary>The time source for this terminal's clocks and timers.</summary>
    internal TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Constructs a terminal and starts its diagnostic case before the pumps can read output, so the case's
    /// checkpoint is the fresh model. The pumps then start as the constructor would have started them. The
    /// caller owns the terminal either way, and disposes it when the case did not start.
    /// </summary>
    internal static (Hex1bTerminal Terminal, DiagnosticCaseResult Case) CreateWithDiagnosticCase(Hex1bTerminalOptions options,
        DiagnosticCaseStartRequest request)
    {
        var startPumps = options.RunCallback == null && !options.DeferStart;
        options.DeferStart = true;
        var terminal = new Hex1bTerminal(options);
        var started = new TerminalDiagnostics(terminal).StartCase(request, DiagnosticCaseStartPath.Construction);
        if (started.Outcome == DiagnosticOutcome.Captured && startPumps)
            terminal.Start();
        return (terminal, started);
    }

    private void CaptureCaseConfiguration(Hex1bTerminalOptions options)
    {
        var reflow = _presentation as Reflow.ITerminalReflowProvider;
        _caseConfiguration = new DiagnosticCaseModelConfiguration
        {
            Width = _width,
            Height = _height,
            ScrollbackCapacity = options.ScrollbackCapacity,
            CommandMarkHistoryCapacity = _commandMarkHistoryCapacity,
            CustomMarkerLimit = _customMarkerLimit,
            EscapeSequenceTimeoutMs = _escapeTimeout.TotalMilliseconds,
            ReflowEnabled = reflow is { ReflowEnabled: true },
            ReflowProvider = reflow is { ReflowEnabled: true } ? reflow.GetType().FullName : null,
            Presentation = _presentation.GetType().FullName ?? "",
            Workload = _workload.GetType().FullName ?? "",
            Capabilities = _presentation.Capabilities.ToString(),
            // The Sixel policy record carries every per-image limit; the per-screen byte budget is separate.
            Graphics = $"{options.CreateSixelPolicy()} MaximumRetainedBytesPerScreen={options.Graphics.MaximumRetainedBytesPerScreen}",
        };
    }

    /// <summary>
    /// Arms a diagnostic case under the model lock. The model is fresh when it has applied no model
    /// event and its pump has read no output bytes. A remote (HMP1) workload's model is driven by state
    /// the case cannot hold, so its checkpoint is unsupported. Returns the armed recorder, or why none was armed.
    /// </summary>
    internal (DiagnosticCaseRecorder? Recorder, string? ProblemCode, string? ActiveCaseId) TryArmDiagnosticCase(
        Func<bool, string?, DiagnosticCaseModelConfiguration, DiagnosticCaseRecorder> create)
    {
        lock (_bufferLock)
        {
            if (_disposed)
                return (null, "target-disposed", null);
            if (_diagnosticCase is { } active)
                return (null, "case-active", active.CaseId);
            var unsupported = _workload is IHmp1TerminalOutputSource
                ? "hmp1-workload: a remote workload's model is driven by state synchronization the case does not hold."
                : null;
            var recorder = create(_modelSequence == 0 && OutputBytesRead == 0, unsupported, _caseConfiguration);
            // Registered with the arming, so the input and frame streams start with the model stream.
            InputMilestones?.SetStreamObserver(recorder);
            Volatile.Write(ref _diagnosticCase, recorder);
            return (recorder, null, null);
        }
    }

    /// <summary>Forgets a finished case so a new one can start.</summary>
    internal void ClearDiagnosticCase(DiagnosticCaseRecorder recorder)
    {
        lock (_bufferLock)
        {
            if (ReferenceEquals(_diagnosticCase, recorder))
                Volatile.Write(ref _diagnosticCase, null);
        }
    }

    // The pump item's original bytes, from its read until the model applies it (or the item ends
    // unapplied). Held whether or not a case is active, so a case started between an item's read and
    // its application still records it: field writes only, no copy. Only an application on the output
    // pump's own async flow takes them: any other application (another thread, or after the pump ended)
    // is recorded without ingress and leaves them alone. The flow is marked once per pump run.
    private static readonly AsyncLocal<Hex1bTerminal?> s_caseIngressPump = new();
    private ReadOnlyMemory<byte> _caseIngress;
    private bool _caseIngressPending;

    private void EnterCaseIngressPump() => s_caseIngressPump.Value = this;

    private void StashCaseIngress(ReadOnlyMemory<byte> data)
    {
        _caseIngress = data;
        _caseIngressPending = true;
    }

    private void ClearCaseIngress()
    {
        _caseIngress = default;
        _caseIngressPending = false;
    }

    // Must hold _bufferLock, right after the model sequence advanced for an application. The first
    // application of a pump item takes that item's bytes; an application with none (a model change not
    // driven by the pump) is recorded as such.
    private void NotifyCaseApplicationUnsafe()
    {
        var pump = ReferenceEquals(s_caseIngressPump.Value, this);
        var pending = pump && _caseIngressPending;
        var ingress = pump ? _caseIngress : default;
        if (pump)
            ClearCaseIngress();
        _diagnosticCase?.BeginApplication(_modelSequence, _width, _height, pending, ingress.Span);
    }

    // Must hold _bufferLock, at the end of an application: graphics resources or placements are state
    // the text checkpoint cannot represent, so re-applicable coverage ends at this application.
    private void NotifyCaseApplicationEndUnsafe() =>
        _diagnosticCase?.EndApplication(_kgpGraphicsState.HasResidentState || _sixelGraphicsState.HasResidentState);

    // Must hold _bufferLock, right after the model sequence advanced for this event.
    private void NotifyCaseModelEventUnsafe(string kind, int width, int height) =>
        _diagnosticCase?.RecordModelEvent(kind, _modelSequence, width, height);

    // Must hold _bufferLock: disposal ends the case before it resets any model state.
    private void NotifyCaseDisposedUnsafe()
    {
        _disposedCase = _diagnosticCase;
        _disposedCase?.RequestStop(DiagnosticCaseStopReason.TargetDisposed);
    }

    // The case disposal stopped. Disposal waits for it (at most the drain bound), so a process that exits
    // right after disposing its terminal still leaves a finished artifact.
    private DiagnosticCaseRecorder? _disposedCase;

    private Task WaitForDisposedCaseAsync() =>
        _disposedCase?.StopAsync(DiagnosticCaseStopReason.TargetDisposed) ?? Task.CompletedTask;
}
