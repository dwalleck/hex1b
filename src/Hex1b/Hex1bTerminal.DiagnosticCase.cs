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

    private void CaptureCaseConfiguration(Hex1bTerminalOptions options, Sixel.SixelCompatibilityPolicy dcsPolicy)
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
            DcsFraming = new DiagnosticCaseDcsFramingConfiguration
            {
                MaximumHeaderParameters = dcsPolicy.MaximumDcsHeaderParameters,
                MaximumNumericValue = dcsPolicy.MaximumNumericValue,
            },
        };
    }

    /// <summary>
    /// Arms a diagnostic case under the model lock. The model is fresh when it has applied no model
    /// event and its pump has read no output bytes. A remote (HMP1) workload's model is driven by state
    /// the case cannot hold, so its checkpoint is unsupported. A model that is not fresh, when the case may
    /// hold state (<paramref name="takeStart"/>), is projected in the same hold: the start checkpoint, so
    /// every model event is either in it or recorded after it. Returns the armed recorder, or why none was armed.
    /// </summary>
    internal (DiagnosticCaseRecorder? Recorder, string? ProblemCode, string? ActiveCaseId) TryArmDiagnosticCase(bool takeStart, long startRoom,
        Func<bool, string?, DiagnosticCaseModelConfiguration, (long Sequence, DiagnosticCaseRecorder.CheckpointCapture Capture)?, DiagnosticCaseRecorder> create)
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
            var fresh = _modelSequence == 0 && OutputBytesRead == 0;
            (long, DiagnosticCaseRecorder.CheckpointCapture)? start = !fresh && takeStart && unsupported is null
                ? (_modelSequence, TakeCompleteCaptureUnsafe(startRoom, "start", DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value))
                : null;
            var recorder = create(fresh, unsupported, _caseConfiguration, start);
            if (start is var (sequence, capture))
                recorder.RecordStartCheckpoint(sequence, capture);
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

            // Counted in progress from before the recording check until the checkpoint is kept, so a stop racing
            // this one (a busy stop, or one that takes no checkpoint, which no caller requests today; the writer's own
            // failure abandons the case rather than closing through the sweep) does not let the writer close past
            // it, up to the closing sweep's 1 s bound.
            recorder.EnterStopCheckpoint();
            try
            {
                if (recorder.LockedStopCheck())
                {
                    var (sequence, capture) = TakeCaseCheckpointUnsafe(recorder, reason == DiagnosticCaseStopReason.SizeLimit);
                    recorder.RecordStopCheckpoint(sequence, capture);
                }
                recorder.StopRecording(reason);
            }
            finally
            {
                recorder.ExitStopCheckpoint();
            }
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
    // estimated before any state is read. A size-limit stop skips projection only when the JSON floor
    // cannot fit what is left of the case; otherwise the writer measures the complete checkpoint line.
    // A projection that fails leaves the boundary without state rather than failing the stop or the mark.
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
        if (forSizeLimitStop && MinimumModelStateJsonBytesUnsafe() > recorder.StopCheckpointRoom)
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

    // A checkpoint that must be complete to count (the start, in the arming hold, or a recovery): refused, without
    // projecting, inside an application, after unapplied output, when it could not fit the case's remaining room, or
    // past the pending-state budget; refused after projecting when its exact size does not fit; never failing the
    // caller. Must hold _bufferLock.
    private DiagnosticCaseRecorder.CheckpointCapture TakeCompleteCaptureUnsafe(long room, string subject, Action? beforeCapture, DiagnosticCaseRecorder? reserveWith = null)
    {
        if (_captureApplicationDepth > 0)
            return new(null, null, "unavailable", $"mid-application: the {subject} was taken inside an application that had not finished", 0);
        if (_continuationUncommitted)
            return new(null, null, "unavailable", "unapplied-output: output was tokenized without being applied since the last application, so its decoder continuation is not the committed one", 0);
        // A line that cannot be written whole would leave the case claiming a checkpoint it never wrote. The geometry's
        // floor refuses, before projecting, a state that cannot fit however small its cells; a recovery then reserves its
        // room from its estimate (the writer bounds what it appends by it) and keeps it at the projected size.
        var tooLarge = $"size-limit: the {subject} state is larger than the case's size bound leaves for its events";
        if (MinimumModelStateJsonBytesUnsafe() + DiagnosticCaseRecorder.StartLineAllowance > room)
            return new(null, null, "unavailable", tooLarge, 0);
        // A start reserves nothing (nothing is pending at arming); a recovery reserves its share with the pending marks'.
        var estimate = EstimateModelStateBytesUnsafe();
        if (reserveWith is null ? estimate > DiagnosticCaseRecorder.PendingStateBudgetInEffect : !reserveWith.TryReserveStateBytes(estimate))
            return new(null, null, "unavailable", $"pending-state budget: the {subject} state is larger than the state a case may hold awaiting its writer", 0);
        if (reserveWith is not null && !reserveWith.TryReserveRecoveryRoom(estimate))
            return new(null, null, "unavailable", tooLarge, estimate);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            beforeCapture?.Invoke();
            var state = CaptureModelState();
            var jsonBytes = SerializedBytes(state);
            var milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (reserveWith is null ? jsonBytes + DiagnosticCaseRecorder.StartLineAllowance > room : !reserveWith.TryKeepRecoveryRoom(jsonBytes))
                return new(null, null, "unavailable", $"{tooLarge} ({jsonBytes} bytes; projected and measured in {milliseconds:0.###} ms)", estimate);
            return new(state, milliseconds, "recorded", null, estimate, jsonBytes);
        }
        catch (Exception error)
        {
            return new(null, null, "unavailable", DiagnosticCaseRecorder.Bounded($"capture-failed: {error.GetType().Name}: {error.Message}"), estimate);
        }
    }

    /// <summary>
    /// Takes a recovery checkpoint in a recording case: a complete <c>text-state/3</c> checkpoint at the current model
    /// sequence, in one hold of the model lock, classified by <paramref name="describe"/> as the engine classifies a
    /// start. A refusal is recorded as a checkpoint line without state; the case keeps recording either way.
    /// </summary>
    internal DiagnosticCaseRecoverResult RecoverDiagnosticCase(DiagnosticCaseRecorder recorder, string? label,
        Func<DiagnosticCaseModelConfiguration, long, DiagnosticCaseRecorder.CheckpointCapture, DiagnosticCaseCheckpoint> describe)
    {
        if (!recorder.IsRecording)
            return RecoverProblem("no-active-case", "No case is recording this terminal.");
        if (!recorder.IncludeModelPayloads)
            return RecoverProblem("requires-reapplication-data", "A recovery checkpoint holds the model's state, which only reapplication-data authorizes.") with { CaseId = recorder.CaseId };
        if (_workload is IHmp1TerminalOutputSource)
            return RecoverProblem("hmp1-workload", "A remote workload's model is driven by state synchronization the case does not hold.") with { CaseId = recorder.CaseId };
        if (!recorder.TryReserveMark())
            return RecoverProblem("busy", $"{DiagnosticCaseRecorder.MaxPendingMarks} checkpoints already await the case's writer.") with { CaseId = recorder.CaseId };
        lock (_bufferLock)
        {
            if (!recorder.IsRecording)
            {
                recorder.AbandonMark();
                return RecoverProblem("no-active-case", "No case is recording this terminal.");
            }

            DiagnosticCaseRecorder.BeforeMarkCaptureForTesting.Value?.Invoke();
            var sequence = recorder.ApplicationInProgress ? _modelSequence - 1 : _modelSequence;
            var capture = TakeCompleteCaptureUnsafe(recorder.CheckpointRoom, "recovery", null, recorder);
            var described = describe(_caseConfiguration, sequence, capture);
            var (ordinal, name) = recorder.RecordRecovery(label, sequence, capture, described);
            var complete = described.Status == DiagnosticCaseCheckpointStatus.Complete;
            return new DiagnosticCaseRecoverResult
            {
                Outcome = complete ? DiagnosticOutcome.Captured : DiagnosticOutcome.Unavailable,
                Problem = complete ? null : new DiagnosticProblem { Code = RefusalCode(described.Reason), Message = described.Reason ?? "unsupported" },
                CaseId = recorder.CaseId,
                Label = name,
                CheckpointOrdinal = ordinal,
                ModelSequence = sequence,
                Status = complete ? "complete" : "unsupported",
                Reason = complete ? null : described.Reason,
                UnsupportedSurfaces = described.UnsupportedSurfaces is { Count: > 0 } surfaces ? surfaces : null,
            };
        }
    }

    // The refusal's kind, as a problem code: the reason's prefix before its colon, in kebab case.
    private static string RefusalCode(string? reason) =>
        reason is null ? "unsupported" : (reason.IndexOf(':') is > 0 and var colon ? reason[..colon] : reason).Replace(' ', '-');

    private static DiagnosticCaseRecoverResult RecoverProblem(string code, string message) => new()
    {
        Outcome = DiagnosticOutcome.Unavailable,
        Problem = new DiagnosticProblem { Code = code, Message = message },
    };

    // The bytes a state serializes to in an event line (the writer's serializer and encoder), counted as the
    // serializer flushes its buffer to the stream, without keeping them.
    private static long SerializedBytes(DiagnosticModelState state)
    {
        using var counter = new ByteCounter();
        System.Text.Json.JsonSerializer.Serialize(counter, state, DiagnosticsJsonContext.Default.DiagnosticModelState);
        return counter.Length;
    }

    // A write-only stream that counts the bytes written to it.
    private sealed class ByteCounter : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }

    // Must hold _bufferLock: a batch was tokenized (moving the decoder, escape prefix and DCS framer) but
    // refused without a model event, so no recorded application reproduces that state change; re-applicable
    // coverage ends at the next model event. Until the next application commits the continuation again, the
    // committed copy lags the live decoder, so a start taken meanwhile cannot hold it.
    private void NotifyCaseUnappliedOutputUnsafe()
    {
        _continuationUncommitted = true;
        _diagnosticCase?.EndInterval(_modelSequence + 1, "unapplied-output");
    }

    // Set by an unapplied tokenization, cleared when an application commits the continuation.
    private bool _continuationUncommitted;

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
    private void NotifyCaseApplicationEndUnsafe(bool sixelIdentified) =>
        _diagnosticCase?.EndApplication(_kgpGraphicsState.HasResidentState || _sixelGraphicsState.HasResidentState, sixelIdentified);

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
