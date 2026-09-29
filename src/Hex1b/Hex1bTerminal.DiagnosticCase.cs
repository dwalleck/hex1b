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
        _caseConfiguration = new DiagnosticCaseModelConfiguration
        {
            Width = _width,
            Height = _height,
            ScrollbackCapacity = options.ScrollbackCapacity,
            CommandMarkHistoryCapacity = _commandMarkHistoryCapacity,
            CustomMarkerLimit = _customMarkerLimit,
            EscapeSequenceTimeoutMs = _escapeTimeout.TotalMilliseconds,
            ReflowEnabled = _presentation is Reflow.ITerminalReflowProvider { ReflowEnabled: true },
            ReflowStrategy = CaseConfiguration.ReflowStrategyId(_presentation),
            Presentation = _presentation.GetType().FullName ?? "",
            Workload = _workload.GetType().FullName ?? "",
            Capabilities = CaseConfiguration.Capabilities(_presentation.Capabilities),
            Graphics = CaseConfiguration.Graphics(options.Graphics),
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
            recorder.SeedModelSequence(_modelSequence);
            // Registered with the arming, so the input and frame streams start with the model stream.
            InputMilestones?.SetStreamObserver(recorder);
            Volatile.Write(ref _diagnosticCase, recorder);
            return (recorder, null, null);
        }
    }

    /// <summary>
    /// Stops a recording case in one hold of the model lock, with its stop checkpoint: no model event falls
    /// between the checkpoint and the stop. The case calls this for every stop but a collector failure.
    /// </summary>
    internal void StopDiagnosticCaseWithCheckpoint(DiagnosticCaseRecorder recorder, DiagnosticCaseStopReason reason)
    {
        // A model lock held for long (a wedged callback: what a case is recorded to diagnose) must not hold up
        // the stop, the time limit or the drain bound: past the wait the case stops without the state.
        var taken = false;
        try
        {
            Monitor.TryEnter(_bufferLock, StopCheckpointLockTimeout, ref taken);
            if (!taken)
            {
                recorder.StopWithoutModelLock(reason,
                    $"model-lock-busy: the model lock was not free within {StopCheckpointLockTimeout.TotalSeconds:0} s");
                return;
            }

            if (recorder.IsRecording)
            {
                var (sequence, capture) = TakeCaseCheckpointUnsafe(recorder, reason == DiagnosticCaseStopReason.SizeLimit);
                recorder.RecordStopCheckpoint(sequence, capture);
            }
            recorder.StopRecording(reason);
        }
        finally
        {
            if (taken)
                Monitor.Exit(_bufferLock);
        }
    }

    /// <summary>How long a stop waits for the model lock before it stops without its checkpoint's state.</summary>
    internal static readonly TimeSpan StopCheckpointLockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Marks a boundary in a recording case: a checkpoint at the current model sequence, taken between two
    /// model events. At most <see cref="DiagnosticCaseRecorder.MaxPendingMarks"/> marks await the writer; a
    /// mark beyond is refused before any state is taken.
    /// </summary>
    internal DiagnosticCaseMarkResult MarkDiagnosticCase(DiagnosticCaseRecorder recorder, string? label)
    {
        if (!recorder.IsRecording)
            return MarkProblem("no-active-case", "No case is recording this terminal.");
        if (!recorder.TryReserveMark())
            return MarkProblem("busy", $"{DiagnosticCaseRecorder.MaxPendingMarks} marks already await the case's writer.") with { CaseId = recorder.CaseId };
        lock (_bufferLock)
        {
            if (!recorder.IsRecording)
            {
                recorder.AbandonMark();
                return MarkProblem("no-active-case", "No case is recording this terminal.");
            }

            DiagnosticCaseRecorder.BeforeMarkCaptureForTesting.Value?.Invoke();
            var (sequence, capture) = TakeCaseCheckpointUnsafe(recorder, forSizeLimitStop: false);
            DiagnosticCaseRecorder.AfterMarkCaptureForTesting.Value?.Invoke();
            var (ordinal, name) = recorder.RecordMark(label, sequence, capture);
            return new DiagnosticCaseMarkResult
            {
                Outcome = DiagnosticOutcome.Captured,
                CaseId = recorder.CaseId,
                Label = name,
                CheckpointOrdinal = ordinal,
                ModelSequence = sequence,
                StateRecorded = capture.State is not null,
                StateReason = capture.State is null ? capture.Reason : null,
            };
        }
    }

    private static DiagnosticCaseMarkResult MarkProblem(string code, string message) => new()
    {
        Outcome = DiagnosticOutcome.Unavailable,
        Problem = new DiagnosticProblem { Code = code, Message = message },
    };

    // Must hold _bufferLock. The state only with reapplication-data and within the pending-state budget,
    // both judged from geometry before any state is read. A size-limit stop skips a state that could not fit
    // what is left of the case. A projection that fails leaves the boundary without state rather than
    // failing the stop or the mark.
    // Returns the checkpoint's model sequence with it: the current one, or, when the checkpoint is re-entered
    // from inside an application (a callback), the boundary only, at the sequence before the application the
    // case has not yet recorded, or at the current one when there is none.
    private (long Sequence, DiagnosticCaseRecorder.CheckpointCapture Capture) TakeCaseCheckpointUnsafe(DiagnosticCaseRecorder recorder,
        bool forSizeLimitStop)
    {
        // Inside any application (a callback's mark or stop, the lock being re-entrant) the model is half
        // applied. While the case has not yet recorded the application, the boundary is the model sequence
        // just before it (0 before the model's first; for a nested application, an event inside the
        // unfinished outer one). Otherwise it is the current one: a nested event recorded the application
        // early, the case was armed inside the callback, or its model stream failed.
        if (_captureApplicationDepth > 0)
            return (recorder.ApplicationInProgress ? _modelSequence - 1 : _modelSequence,
                new(null, null, "unavailable", "mid-application: taken inside an application that had not finished", 0));
        if (!recorder.IncludeModelPayloads)
            return (_modelSequence, new(null, null, "unavailable", "requires reapplication-data", 0));
        if (forSizeLimitStop && EstimateModelStateJsonBytesUnsafe() > recorder.StopCheckpointRoom)
            return (_modelSequence, new(null, null, "missing", "size-limit", 0));
        var estimate = EstimateModelStateBytesUnsafe();
        if (!recorder.TryReserveStateBytes(estimate))
            return (_modelSequence, new(null, null, "unavailable", "pending-state budget: the checkpoints awaiting the writer already hold their share of state", 0));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var state = CaptureModelState();
            return (_modelSequence, new(state, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, "recorded", null, estimate));
        }
        catch (Exception error)
        {
            recorder.ReleaseStateBytes(estimate);
            return (_modelSequence, new(null, null, "unavailable", DiagnosticCaseRecorder.Bounded($"capture-failed: {error.GetType().Name}: {error.Message}"), 0));
        }
    }

    // Must hold _bufferLock: a batch was tokenized (moving the decoder, escape prefix and DCS framer) but
    // refused without a model event, so no recorded application reproduces that state change; re-applicable
    // coverage ends at the next model event.
    private void NotifyCaseUnappliedOutputUnsafe() =>
        _diagnosticCase?.EndInterval(_modelSequence + 1, "unapplied-output");

    /// <summary>
    /// Forgets a finished case so a new one can start. Atomic without the model lock, so a finishing writer
    /// never waits on a held model lock (arming checks the field under the lock, and every hook reads it once).
    /// </summary>
    internal void ClearDiagnosticCase(DiagnosticCaseRecorder recorder) =>
        Interlocked.CompareExchange(ref _diagnosticCase, null, recorder);

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
        CommitOutputContinuationUnsafe();
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

    // A disposal re-entered under the model lock (a scrollback or title callback that disposes) cannot wait:
    // the writer needs that lock to finish, so it would stall for the whole drain bound. The case still stops.
    private Task WaitForDisposedCaseAsync() =>
        _disposedCase is { } recorder && !Monitor.IsEntered(_bufferLock)
            ? recorder.StopAsync(DiagnosticCaseStopReason.TargetDisposed)
            : Task.CompletedTask;
}
