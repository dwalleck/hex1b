using System.Diagnostics;
using System.Runtime.CompilerServices;
using Hex1b.Input;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// One active diagnostic case on a terminal. Producers offer events without waiting; a dedicated writer
/// drains the bounded queue into the artifact and pulls native delivery records itself. The terminal
/// holds at most one recorder, armed and cleared under its model lock.
/// </summary>
internal sealed class DiagnosticCaseRecorder : IDiagnosticStreamObserver
{
    internal const long DefaultMaxBytes = 64L * 1024 * 1024;
    internal const int DefaultMaxSeconds = 10 * 60;
    internal const long MinMaxBytes = 1024L * 1024;
    internal const long MaxMaxBytes = 1024L * 1024 * 1024;
    internal const int MinMaxSeconds = 1;
    internal const int MaxMaxSeconds = 24 * 60 * 60;

    /// <summary>Counts ingress copies made in the current async flow while a test has set a counter.</summary>
    internal static readonly AsyncLocal<StrongBox<int>?> IngressCopiesForTesting = new();

    /// <summary>
    /// A gate the writer waits on before each pass, taken from the arming flow while a test has set it,
    /// so a test can hold the writer (to force overload or eviction) and release it.
    /// </summary>
    internal static readonly AsyncLocal<ManualResetEventSlim?> WriterGateForTesting = new();

    private readonly ManualResetEventSlim? _writerGate = WriterGateForTesting.Value;

    /// <summary>An exception the writer throws on its next event, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> WriterFaultForTesting = new();

    /// <summary>A stream whose next offered event fails, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<string?> StreamFaultForTesting = new();

    /// <summary>Runs just before a start arms its case, on the starting flow, while a test has set it.</summary>
    internal static readonly AsyncLocal<Action?> BeforeArmForTesting = new();

    /// <summary>Runs after a start armed its case and before its writer starts, while a test has set it.</summary>
    internal static readonly AsyncLocal<Action?> AfterArmForTesting = new();

    /// <summary>
    /// Runs on the producer's thread after an offer passed its recording check and before it enqueues,
    /// taken from the arming flow while a test has set it (to hold a producer across a stop).
    /// </summary>
    internal static readonly AsyncLocal<Action?> BeforeEnqueueForTesting = new();

    /// <summary>An exception the writer throws before its next loss range, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> LossFaultForTesting = new();

    /// <summary>An exception the writer throws before its next delivery record, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> DeliveryFaultForTesting = new();

    /// <summary>
    /// An exception the writer throws before every loss range (not once), taken from the arming flow while a test
    /// has set it: storage that keeps failing for those lines.
    /// </summary>
    internal static readonly AsyncLocal<Exception?> PersistentLossFaultForTesting = new();

    /// <summary>
    /// Runs on the writer after a failure marked the stop and before it declares what was not written (a static
    /// hook, read at that moment, since the writer does not run on the test's flow).
    /// </summary>
    internal static readonly StrongBox<Action<DiagnosticCaseRecorder>?> AfterStopMarkForTesting = new();

    /// <summary>An exception the writer throws once, declaring unpulled delivery, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> UnpulledFaultForTesting = new();

    /// <summary>An exception the writer's disposal throws, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> WriterDisposeFaultForTesting = new();

    /// <summary>
    /// Runs on the marking thread inside the model lock, right after a mark took its state, while a test has
    /// set it (to compare the mark with the model in the same locked section).
    /// </summary>
    internal static readonly AsyncLocal<Action?> AfterMarkCaptureForTesting = new();

    /// <summary>
    /// Runs on the marking thread inside the model lock, before the mark takes its state, while a test has set
    /// it (a test can change the model there, re-entering the lock, to show the state is taken in that hold).
    /// </summary>
    internal static readonly AsyncLocal<Action?> BeforeMarkCaptureForTesting = new();

    /// <summary>
    /// Runs on the producer's thread as a model event's offer ends, just after its in-flight count is released,
    /// taken from the arming flow while a test has set it.
    /// </summary>
    internal static readonly AsyncLocal<Action?> AfterModelOfferForTesting = new();

    private readonly Action? _afterModelOffer = AfterModelOfferForTesting.Value;

    /// <summary>At most this many marks await the writer; a mark beyond is refused (design approval item 2).</summary>
    internal const int MaxPendingMarks = 64;

    /// <summary>
    /// Pending checkpoint state is also bounded in bytes (approval item 2, amended): a checkpoint whose estimated
    /// state would pass the budget records the boundary only.
    /// </summary>
    internal const long PendingStateBudget = 256L * 1024 * 1024;

    /// <summary>A smaller pending-state budget, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<long?> PendingStateBudgetForTesting = new();

    private readonly long _pendingStateBudget = PendingStateBudgetForTesting.Value ?? PendingStateBudget;

    /// <summary>
    /// A checkpoint as the terminal took it: its state (or none), how long the lock was held, its status and
    /// the reason it has no state, and the pending-state bytes reserved for it.
    /// </summary>
    internal readonly record struct CheckpointCapture(DiagnosticModelState? State, double? Milliseconds, string Status, string? Reason,
        long StateBytes);

    /// <summary>A shorter drain bound for tests, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<TimeSpan?> DrainTimeoutForTesting = new();

    /// <summary>
    /// Test-only: milliseconds the writer waits after the manifest before its first pass, so an end-to-end
    /// harness can overload a real target's queue. Read once per case; ignored unless a positive integer
    /// below the drain bound.
    /// </summary>
    internal const string WriterHoldEnvironmentVariable = "HEX1B_DIAGNOSTIC_CASE_TEST_WRITER_HOLD_MS";

    private static TimeSpan ReadWriterHold() =>
        int.TryParse(Environment.GetEnvironmentVariable(WriterHoldEnvironmentVariable), out var ms) && ms is > 0 and < 10_000
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.Zero;

    private readonly TimeSpan _writerHold = ReadWriterHold();

    /// <summary>How long a stop waits for queued events to reach the artifact (spec Q11).</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    // The size bound is kept in tiers, so what must be written always has room:
    //   events and running loss ranges                 below MaxBytes - 16 KiB (EventLimit);
    //   ranges written as the case closes (discards)   below MaxBytes - 8 KiB  (RangeLimit);
    //   closing records: the interval end, at most one failure and one overflow summary per stream,
    //   each a bounded line                             below MaxBytes - 2 KiB  (ClosingLimit);
    //   the completion record                          in the rest.
    internal const long EventReserve = 16 * 1024;
    internal const long RangeReserve = 8 * 1024;
    internal const long ClosingReserve = 2 * 1024;
    internal const int MaxFailureMessage = 256;

    private Exception? _writerFault = WriterFaultForTesting.Value;
    private Exception? _writerDisposeFault = WriterDisposeFaultForTesting.Value;
    private Exception? _lossFault = LossFaultForTesting.Value;
    private Exception? _deliveryFault = DeliveryFaultForTesting.Value;
    private Exception? _unpulledFault = UnpulledFaultForTesting.Value;
    private readonly Exception? _persistentLossFault = PersistentLossFaultForTesting.Value;
    private readonly Action? _beforeEnqueue = BeforeEnqueueForTesting.Value;
    private string? _streamFault = StreamFaultForTesting.Value;
    private readonly TimeSpan _drainTimeout = DrainTimeoutForTesting.Value ?? DrainTimeout;

    private const int Recording = 0;
    private const int Stopping = 1;
    private const int Stopped = 2;
    private const int StreamCount = 5;

    private readonly CaseEventQueue _queue = new();
    private readonly CaseLossLedger _loss = new();
    private readonly CaseArtifactWriter _writer;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TimeProvider _timeProvider;
    private readonly Action<DiagnosticCaseRecorder> _finished;
    private readonly CaseSources _sources;
    private readonly bool _rawInput;
    private readonly bool _editorText;
    private readonly bool _nativeOutput;
    private readonly long[] _ordinals = new long[StreamCount];
    private readonly long[] _offered = new long[StreamCount];
    private readonly long[] _written = new long[StreamCount];
    private readonly long[] _dropped = new long[StreamCount];
    private int _state;
    // Assigned by the writer as it writes, so case sequences are dense and ordered in the file.
    private long _caseSequence;
    private long _lastWritten = -1;
    private int _stopReason = -1;
    private DateTimeOffset? _stoppedAt;
    // Completed when the writer has finished. It exists from construction, so a stop that finds the recorder
    // armed but not yet started still waits for the artifact.
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Producers between their recording check and their enqueue; the writer's last pass waits for them.
    private int _offersInFlight;
    // The application begun under the model lock and offered when it ends, after its graphics check.
    private (long ModelSequence, int Width, int Height, int Length, bool HasIngress, byte[]? Payload)? _application;
    // Per stream, the first ordinal of a missing range that did not fit under the size bound. From there the
    // stream's coverage is summarized as one range of unknown extent when the case closes.
    private readonly long?[] _undescribedFrom = new long?[StreamCount];

    // Where re-applicable model coverage ends. Held outside the queue so overload cannot drop it, and
    // written before any model event at or after it.
    private long _intervalEnd = long.MaxValue;
    private string? _intervalEndReason;
    private bool _intervalEndWritten;

    // The last native delivery record already pulled (records up to it predate the case or are written).
    private long _deliverySince;

    // Set when a stop gave up waiting for the writer: whatever it has not written is declared missing.
    private int _drainAbandoned;
    private ITimer? _timeLimit;

    // Checkpoints taken and not yet written: marks in the order taken, and at most one stop checkpoint. Held
    // outside the queue, so overload cannot drop them; each is written after the model events before it.
    private readonly System.Collections.Concurrent.ConcurrentQueue<PendingCheckpoint> _checkpoints = new();
    private PendingCheckpoint? _stopCheckpoint;
    private bool _stopCheckpointSettled;
    // Marks reserved or awaiting the writer; bounded, and reserved before any state is taken.
    private int _pendingMarks;
    // Marks between their reservation and their enqueue (or abandonment); every closing sweep waits for none,
    // so a mark that passed its in-lock check is written or declared, never lost.
    private int _marksInProgress;
    // Estimated bytes of checkpoint state taken and not yet written.
    private long _pendingStateBytes;
    private long _lastOfferedModelSequence;

    /// <summary>
    /// The last model event offered to the case: the boundary a checkpoint names when it cannot read the model
    /// (a wedged model lock), since an application begun but not yet offered is not in the artifact.
    /// </summary>
    internal long LastOfferedModelSequence => Volatile.Read(ref _lastOfferedModelSequence);

    /// <summary>
    /// Stops the case without the model lock (it was held too long), with a stop checkpoint that records the
    /// boundary only. The stop is marked first and offers already past their check are waited for, so the
    /// boundary read afterwards is the last model event the case records. The checkpoint counts as in
    /// progress until recorded, so the closing sweep waits for it.
    /// </summary>
    internal void StopWithoutModelLock(DiagnosticCaseStopReason reason, string checkpointReason)
    {
        // Counted in progress before the recording check (both full fences), as a mark is: either this sees the
        // case already stopped, or every closing sweep sees this checkpoint in progress and waits for it (up to
        // its bound).
        EnterStopCheckpoint();
        try
        {
            var recording = IsRecording;
            _afterBusyStopCheck?.Invoke();
            StopRecording(reason);
            if (!recording)
                return;
            WaitForOffersInFlight();
            RecordStopCheckpoint(LastOfferedModelSequence, new CheckpointCapture(null, null, "unavailable", checkpointReason, 0));
        }
        finally
        {
            ExitStopCheckpoint();
        }
    }

    /// <summary>
    /// Runs in <see cref="StopWithoutModelLock"/> after its recording check, taken from the arming flow while a
    /// test has set it.
    /// </summary>
    internal static readonly AsyncLocal<Action?> AfterBusyStopCheckForTesting = new();

    private readonly Action? _afterBusyStopCheck = AfterBusyStopCheckForTesting.Value;

    /// <summary>Starts the last offered sequence at the model's sequence when armed (under the model lock).</summary>
    internal void SeedModelSequence(long modelSequence) => Volatile.Write(ref _lastOfferedModelSequence, modelSequence);

    /// <summary>
    /// Whether an application has begun and not yet been offered. The caller holds the model lock: a checkpoint
    /// taken then (re-entered from inside the application) would see it half applied.
    /// </summary>
    internal bool ApplicationInProgress => _application is not null;
    private long _checkpointsTaken;
    private long _checkpointsWritten;
    private long _checkpointsDropped;

    private readonly record struct PendingCheckpoint(long ModelSequence, DiagnosticCaseCheckpointEvent Checkpoint, long StateBytes);

    // Streams that failed stop recording; their failure is written outside the queue.
    private readonly bool[] _failed = new bool[StreamCount];
    private readonly System.Collections.Concurrent.ConcurrentQueue<DiagnosticCaseRecord> _streamFailures = new();

    internal DiagnosticCaseRecorder(DiagnosticCaseManifest manifest, string path, TimeProvider timeProvider,
        Action<DiagnosticCaseRecorder> finished, CaseSources sources)
    {
        Manifest = manifest;
        Path = path;
        _timeProvider = timeProvider;
        _finished = finished;
        _sources = sources;
        _writer = new CaseArtifactWriter(path);
        IncludeModelPayloads = manifest.Authorizations.Contains(DiagnosticAuthorization.ReapplicationData);
        _rawInput = manifest.Authorizations.Contains(DiagnosticAuthorization.RawInput);
        _editorText = manifest.Authorizations.Contains(DiagnosticAuthorization.EditorText);
        _nativeOutput = manifest.Authorizations.Contains(DiagnosticAuthorization.NativeOutput);
        _deliverySince = sources.Delivery?.Read(long.MaxValue - 1, 1, includeBytes: false).Totals.LastSequence ?? 0;
        _deliveryOfferedThrough = _deliverySince;
    }

    internal DiagnosticCaseManifest Manifest { get; }

    internal string Path { get; }

    internal string CaseId => Manifest.CaseId;

    /// <summary>Whether model events carry their original input bytes (<c>reapplication-data</c>).</summary>
    internal bool IncludeModelPayloads { get; }

    internal bool IsRecording => Volatile.Read(ref _state) == Recording;

    /// <summary>The model sequence where re-applicable coverage ends, or <see cref="long.MaxValue"/>.</summary>
    internal long IntervalEnd => Volatile.Read(ref _intervalEnd);

    /// <summary>The loss ledger, for tests that need more loss ranges than a real overload produces.</summary>
    internal CaseLossLedger LossForTesting => _loss;

    /// <summary>Completes when the writer has finished and the completion record is written (or failed).</summary>
    internal Task Completion => _completion.Task;

    /// <summary>Starts the writer and the time bound; called once, after the terminal armed this recorder.</summary>
    internal void Start()
    {
        _timeLimit = _timeProvider.CreateTimer(_ => RequestStop(DiagnosticCaseStopReason.TimeLimit), null,
            TimeSpan.FromSeconds(Manifest.Bounds.MaxSeconds), Timeout.InfiniteTimeSpan);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Begins an output application: copies its original input bytes (only under <c>reapplication-data</c>)
    /// while they are valid. The event is offered by <see cref="EndApplication"/>, once the application's
    /// effect on graphics state is known, so an interval end it causes precedes it in the artifact. The caller
    /// holds the model lock for the whole application.
    /// </summary>
    internal void BeginApplication(long modelSequence, int width, int height, bool hasIngress, ReadOnlySpan<byte> ingress)
    {
        FlushReentered("reentrant-application");
        if (!IsRecording || Volatile.Read(ref _failed[(int)CaseStream.Model]))
            return;
        try
        {
            byte[]? payload = null;
            if (IncludeModelPayloads && hasIngress)
            {
                payload = ingress.ToArray();
                if (IngressCopiesForTesting.Value is { } copies)
                    Interlocked.Increment(ref copies.Value);
            }

            _application = (modelSequence, width, height, ingress.Length, hasIngress, payload);
        }
        catch (Exception error)
        {
            FailModel(modelSequence, error);
        }
    }

    /// <summary>
    /// Offers the application begun by <see cref="BeginApplication"/>. Graphics state the checkpoint cannot
    /// hold, or an application without input bytes, ends re-applicable coverage at it.
    /// </summary>
    internal void EndApplication(bool graphics)
    {
        if (_application is not { } application)
            return;
        _application = null;
        // A model change the case holds no input for cannot be reproduced: coverage ends here.
        if (!application.HasIngress)
            EndInterval(application.ModelSequence, "application-without-ingress");
        if (graphics)
            EndInterval(application.ModelSequence, "graphics");
        OfferModel(application.HasIngress ? "application" : "application-without-ingress", application.ModelSequence,
            application.Width, application.Height, application.Length, application.Payload);
    }

    /// <summary>Records a model event. The caller holds the terminal's model lock, which orders model events.</summary>
    internal void RecordModelEvent(string kind, long modelSequence, int width, int height)
    {
        FlushReentered("reentrant-model-event");
        OfferModel(kind, modelSequence, width, height, null, null);
    }

    // A model event raised while an application is still in progress (a handler that resizes, or a nested
    // application) happened in the middle of it: re-applying the application's bytes and then the event cannot
    // reproduce that. The open application is offered now, so model events stay in sequence order, and
    // re-applicable coverage ends at it.
    private void FlushReentered(string reason)
    {
        if (_application is not { } application)
            return;
        _application = null;
        EndInterval(application.ModelSequence, reason);
        OfferModel(application.HasIngress ? "application" : "application-without-ingress", application.ModelSequence,
            application.Width, application.Height, application.Length, application.Payload);
    }

    // A model-stream failure must never reach the terminal, whose model sequence has already advanced: the
    // stream ends, and re-applicable coverage with it.
    private void OfferModel(string kind, long modelSequence, int width, int height, int? length, byte[]? payload)
    {
        if (!IsRecording || Volatile.Read(ref _failed[(int)CaseStream.Model]))
            return;
        try
        {
            Offer(CaseStream.Model, kind, modelSequence, width, height, length, payload, null);
        }
        catch (Exception error)
        {
            FailModel(modelSequence, error);
        }
    }

    private void FailModel(long modelSequence, Exception error)
    {
        EndInterval(modelSequence, "stream-failed");
        FailStream(CaseStream.Model, error);
    }

    /// <summary>
    /// Ends re-applicable coverage at <paramref name="modelSequence"/> (the first event that cannot be
    /// reproduced). The first end wins. The caller holds the model lock.
    /// </summary>
    internal void EndInterval(long modelSequence, string reason)
    {
        if (modelSequence >= Volatile.Read(ref _intervalEnd))
            return;
        _intervalEndReason = reason;
        Volatile.Write(ref _intervalEnd, modelSequence);
    }

    // The tracker calls these under its own lock from the application's threads: a failure here must end
    // only this stream, never reach the application.
    void IDiagnosticStreamObserver.OnInputAccepted(long id, string kind, string source, Hex1bEvent? evt) =>
        Observe(CaseStream.Input, () => Offer(CaseStream.Input, "accepted", null, null, null, null, null, new DiagnosticCaseInputEvent
        {
            Id = id,
            InputKind = kind,
            Source = source,
            Payload = _rawInput && evt is not null ? InputMilestoneTracker.PayloadOf(evt) : null,
        }));

    void IDiagnosticStreamObserver.OnInputProcessed(long id, string applicationInstanceId, long watermark) =>
        Observe(CaseStream.Input, () => Offer(CaseStream.Input, "processed", null, null, null, null, null, new DiagnosticCaseInputEvent
        {
            Id = id,
            ProcessedBy = applicationInstanceId,
            Watermark = watermark,
        }));

    void IDiagnosticStreamObserver.OnFramePublished(string applicationInstanceId, long frameId, long processedInput, bool wroteOutput,
        long? outputMark) => Observe(CaseStream.Frames, () =>
    {
        // The application writes the frame before it reports the publication, on the same thread.
        var published = _sources.Frames?.LatestFrame is { } latest && latest.FrameId == frameId ? latest : null;
        var projection = published?.Frame is { } frame ? (_editorText ? frame : TerminalDiagnostics.WithoutEditorText(frame)) : null;
        Offer(CaseStream.Frames, "published", null, null, null, null, null, detailBytes: CaseFrameSize.Estimate(projection), detail: new DiagnosticCaseFrameEvent
        {
            FrameId = frameId,
            ApplicationInstanceId = applicationInstanceId,
            ProcessedInput = processedInput,
            WroteOutput = wroteOutput,
            OutputMark = outputMark,
            Projection = projection,
            Failure = published is null ? "The published projection was already replaced." : published.Failure,
        });
    });

    private void Observe(CaseStream stream, Action record)
    {
        if (!IsRecording || Volatile.Read(ref _failed[(int)stream]))
            return;
        try
        {
            record();
        }
        catch (Exception error)
        {
            FailStream(stream, error);
        }
    }

    // Ends one stream's coverage; the others keep recording. The failure is written outside the queue.
    private void FailStream(CaseStream stream, Exception error)
    {
        if (Volatile.Read(ref _failed[(int)stream]))
            return;
        Volatile.Write(ref _failed[(int)stream], true);
        _streamFailures.Enqueue(new DiagnosticCaseRecord
        {
            Stream = CaseArtifactWriter.StreamName(stream),
            FromOrdinal = Interlocked.Read(ref _ordinals[(int)stream]) + 1,
            Reason = Bounded($"stream-failed: {error.GetType().Name}: {error.Message}"),
        });
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    // A failure message is bounded in bytes, not just characters, so the closing tier's budget holds: at most
    // MaxFailureMessage printable ASCII characters (anything else becomes '?', which JSON never escapes).
    internal static string Bounded(string text)
    {
        var length = Math.Min(text.Length, MaxFailureMessage);
        return string.Create(length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = source[i] is >= ' ' and <= '~' and not '"' and not '\\' and not '<' and not '>' and not '&' and not '\'' and not '+' and not '`' ? source[i] : '?';
        });
    }

    private void Offer(CaseStream stream, string kind, long? modelSequence, int? width, int? height, int? length, byte[]? payload,
        object? detail = null, int detailBytes = 0)
    {
        // Counted in flight before the recording check (both full fences), so the writer's last pass, which
        // runs after the stop and waits for no offer in flight, sees every event offered while recording.
        Interlocked.Increment(ref _offersInFlight);
        try
        {
            if (Volatile.Read(ref _state) != Recording)
                return;
            var index = (int)stream;
            if (_streamFault is { } faulted && faulted == CaseArtifactWriter.StreamName(stream) && Interlocked.CompareExchange(ref _streamFault, null, faulted) == faulted)
                throw new InvalidOperationException("Injected stream failure.");
            _beforeEnqueue?.Invoke();
            Interlocked.Increment(ref _offered[index]);
            // Published inside the in-flight window, so a stop that waits for offers in flight sees it.
            if (stream == CaseStream.Model && modelSequence is { } offeredModelSequence)
                Volatile.Write(ref _lastOfferedModelSequence, offeredModelSequence);
            var ordinal = Interlocked.Increment(ref _ordinals[index]);
            var item = new CaseEvent(stream, ordinal, Stopwatch.GetTimestamp(), kind, modelSequence, width, height, length, payload, detail, detailBytes);
            if (_queue.TryEnqueue(item))
            {
                if (_signal.CurrentCount == 0)
                    _signal.Release();
            }
            else
            {
                // Drop-newest; the loss is recorded outside the queue that could not take the event. Status
                // counts it at once; the completion counts it only once its range is declared.
                Interlocked.Increment(ref _dropped[index]);
                if (!Volatile.Read(ref _overloadUnknownDeclared[index]))
                    Interlocked.Increment(ref _undeclaredOverload[index]);
                _loss.Record(stream, ordinal);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _offersInFlight);
            if (stream == CaseStream.Model)
                _afterModelOffer?.Invoke();
        }
    }

    /// <summary>Reserves a place for a mark; false when <see cref="MaxPendingMarks"/> already await the writer.</summary>
    internal bool TryReserveMark()
    {
        while (true)
        {
            var pending = Volatile.Read(ref _pendingMarks);
            if (pending >= MaxPendingMarks)
                return false;
            if (Interlocked.CompareExchange(ref _pendingMarks, pending + 1, pending) == pending)
            {
                Interlocked.Increment(ref _marksInProgress);
                return true;
            }
        }
    }

    /// <summary>Returns a reservation that recorded no mark.</summary>
    internal void AbandonMark()
    {
        Interlocked.Decrement(ref _pendingMarks);
        Interlocked.Decrement(ref _marksInProgress);
    }

    // A written or declared mark frees its place.
    private void ReleaseMarkSlot() => Interlocked.Decrement(ref _pendingMarks);

    /// <summary>
    /// Reserves pending-state bytes for a checkpoint's state; false when the budget would be passed. Nothing is
    /// reserved then, and the checkpoint records the boundary only.
    /// </summary>
    internal bool TryReserveStateBytes(long bytes)
    {
        while (true)
        {
            var pending = Volatile.Read(ref _pendingStateBytes);
            if (pending + bytes > _pendingStateBudget)
                return false;
            if (Interlocked.CompareExchange(ref _pendingStateBytes, pending + bytes, pending) == pending)
                return true;
        }
    }

    /// <summary>Returns reserved pending-state bytes.</summary>
    internal void ReleaseStateBytes(long bytes) => Interlocked.Add(ref _pendingStateBytes, -bytes);

    /// <summary>Bytes left in the tier the stop checkpoint is written in, so a stop can skip a state that cannot fit.</summary>
    internal long StopCheckpointRoom => RangeLimit - _writer.BytesWritten;

    /// <summary>
    /// Records a reserved mark's checkpoint. The caller holds the model lock, so every model event before it
    /// has been offered, and the case is recording. Returns its ordinal and label.
    /// </summary>
    internal (long Ordinal, string Label) RecordMark(string? label, long modelSequence, CheckpointCapture capture)
    {
        var ordinal = Interlocked.Increment(ref _checkpointsTaken);
        var name = label ?? $"mark-{ordinal}";
        _checkpoints.Enqueue(new PendingCheckpoint(modelSequence, Checkpoint(ordinal, name, "mark", capture), capture.StateBytes));
        Interlocked.Decrement(ref _marksInProgress);
        if (_signal.CurrentCount == 0)
            _signal.Release();
        return (ordinal, name);
    }

    /// <summary>
    /// Records the stop checkpoint: from a stop holding the model lock (which stops the case in the same hold),
    /// or from a stop that could not take it (<see cref="StopWithoutModelLock"/>). The first claim wins
    /// atomically, as the two can race; a later one returns its reserved state bytes. The caller is counted in
    /// progress (<see cref="EnterStopCheckpoint"/>) from before its recording check until this returns, so a
    /// closing sweep never finds a checkpoint claimed but not yet kept.
    /// </summary>
    internal void RecordStopCheckpoint(long modelSequence, CheckpointCapture capture)
    {
        if (Interlocked.CompareExchange(ref _stopCheckpointClaimed, 1, 0) != 0)
        {
            ReleaseStateBytes(capture.StateBytes);
            return;
        }
        _afterStopCheckpointClaim?.Invoke();
        _stopCheckpoint = new PendingCheckpoint(modelSequence,
            Checkpoint(Interlocked.Increment(ref _checkpointsTaken), "stop", "stop", capture), capture.StateBytes);
    }

    private int _stopCheckpointClaimed;

    /// <summary>
    /// Counts a stop's checkpoint in progress, as a mark is, until <see cref="ExitStopCheckpoint"/>: the closing
    /// sweep waits for it (bounded, as the stop holds the model lock only while it takes its state).
    /// </summary>
    internal void EnterStopCheckpoint() => Interlocked.Increment(ref _marksInProgress);

    /// <summary>Ends <see cref="EnterStopCheckpoint"/>.</summary>
    internal void ExitStopCheckpoint() => Interlocked.Decrement(ref _marksInProgress);

    /// <summary>
    /// Runs after a stop checkpoint's claim, before it is kept, taken from the arming flow while a test has
    /// set it.
    /// </summary>
    internal static readonly AsyncLocal<Action?> AfterStopCheckpointClaimForTesting = new();

    private readonly Action? _afterStopCheckpointClaim = AfterStopCheckpointClaimForTesting.Value;

    private static DiagnosticCaseCheckpointEvent Checkpoint(long ordinal, string label, string trigger, CheckpointCapture capture) => new()
    {
        Ordinal = ordinal,
        Label = label,
        Trigger = trigger,
        Status = capture.State is null ? capture.Status : "recorded",
        Reason = capture.State is null ? capture.Reason ?? "unavailable" : null,
        CaptureMilliseconds = capture.State is null ? null : capture.Milliseconds,
        State = capture.State,
    };

    /// <summary>
    /// Asks the case to stop without waiting; the first reason wins. While recording, the terminal stops it
    /// in one hold of the model lock with its stop checkpoint (a collector failure takes none).
    /// </summary>
    internal void RequestStop(DiagnosticCaseStopReason reason)
    {
        if (reason != DiagnosticCaseStopReason.CollectorFailed && IsRecording && _sources.StopWithCheckpoint is { } stop)
        {
            try
            {
                stop(this, reason);
            }
            finally
            {
                // The callback stops the case; this is a no-op then, and stops it if the callback failed first.
                StopRecording(reason);
            }
            return;
        }

        StopRecording(reason);
    }

    /// <summary>
    /// Stops recording: the first reason wins. The terminal's stop callback calls this under the model lock, or
    /// through <see cref="StopWithoutModelLock"/> when it could not take it.
    /// </summary>
    internal void StopRecording(DiagnosticCaseStopReason reason)
    {
        Interlocked.CompareExchange(ref _stopReason, (int)reason, -1);
        // Marked before the state changes, so a writer that sees the stop always sees the mark (first mark wins).
        if (Volatile.Read(ref _state) == Recording)
            MarkDeliveryAtStop();
        if (Interlocked.CompareExchange(ref _state, Stopping, Recording) == Recording)
            _signal.Release();
    }

    // The last delivery record the terminal made before the case stopped; later records are not the case's.
    private long _deliveryAtStop = long.MaxValue;

    private void MarkDeliveryAtStop() =>
        Interlocked.CompareExchange(ref _deliveryAtStop,
            _sources.Delivery?.Read(long.MaxValue - 1, 1, includeBytes: false).Totals.LastSequence ?? 0, long.MaxValue);

    /// <summary>
    /// Stops the case and waits (at most the drain bound) for its writer; returns the reason it stopped
    /// with. A writer that has not finished by then abandons its unwritten events as missing.
    /// </summary>
    internal async Task<DiagnosticCaseStopReason> StopAsync(DiagnosticCaseStopReason reason)
    {
        RequestStop(reason);
        if (!Completion.IsCompleted)
        {
            await Task.WhenAny(Completion, Task.Delay(_drainTimeout)).ConfigureAwait(false);
            if (!Completion.IsCompleted)
                Volatile.Write(ref _drainAbandoned, 1);
        }

        return (DiagnosticCaseStopReason)Volatile.Read(ref _stopReason);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            _writer.WriteManifest(Manifest);
            if (_writerHold > TimeSpan.Zero)
                await Task.Delay(_writerHold).ConfigureAwait(false);
            while (true)
            {
                _writerGate?.Wait();
                var stopping = Volatile.Read(ref _state) != Recording;
                // Checkpoints taken before this pass: their model events were offered before them, so they are
                // in the queue drained below (or declared lost) and precede them in the artifact.
                var readyCheckpoints = _checkpoints.Count;
                // Loss and failures first: they do not depend on the queue draining.
                WriteLoss(final: false);
                WriteStreamFailures();
                if (Volatile.Read(ref _drainAbandoned) == 1)
                {
                    WaitForOffersInFlight();
                    DeclareUnpulledDelivery("drain-timeout");
                    DiscardQueued("drain-timeout");
                    break;
                }

                var drained = DrainQueue();
                if (!drained || !PullDelivery() || !WriteCheckpoints(readyCheckpoints, EventLimit))
                {
                    // The size bound: the case ends, and what it could not write is missing, delivery records
                    // the terminal made before the stop included.
                    RequestStop(DiagnosticCaseStopReason.SizeLimit);
                    WaitForOffersInFlight();
                    DeclareUnpulledDelivery("size-limit");
                    DiscardQueued("size-limit");
                    break;
                }

                _writer.Flush();
                // In flight first: an offer enqueues before it leaves, so a queue read after seeing none in
                // flight sees its event.
                if (stopping && Volatile.Read(ref _offersInFlight) == 0 && _queue.Count == 0)
                    break;
                if (stopping)
                    await Task.Delay(1).ConfigureAwait(false);
                else
                    await _signal.WaitAsync(50).ConfigureAwait(false);
            }

            WriteClosingRecords();
            _stoppedAt = _timeProvider.GetUtcNow();
            _writer.Flush();
            _writer.WriteCompletion(DescribeCompletion());
        }
        catch (Exception)
        {
            // The writer or its storage failed: the case stops. What was never written is declared missing
            // and the completion is attempted; with storage failing either may not exist, and the artifact
            // then reads as interrupted. The reader also marks any stream whose completion counts show
            // unaccounted events.
            Interlocked.Exchange(ref _stopReason, (int)DiagnosticCaseStopReason.CollectorFailed);
            // A full fence, as RequestStop's: an offer counts itself in flight before it reads the state.
            MarkDeliveryAtStop();
            Interlocked.Exchange(ref _state, Stopping);
            _stoppedAt ??= _timeProvider.GetUtcNow();
            WaitForOffersInFlight();
            AfterStopMarkForTesting.Value?.Invoke(this);
            // Separately, so a failure declaring one leaves the other to be declared (or reported unaccounted).
            try
            {
                DeclareUnpulledDelivery("collector-failed");
            }
            catch (Exception)
            {
            }

            try
            {
                DiscardQueued("collector-failed");
            }
            catch (Exception)
            {
            }

            try
            {
                _writer.WriteCompletion(DescribeCompletion());
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            Volatile.Write(ref _state, Stopped);
            try
            {
                _timeLimit?.Dispose();
                _sources.Input?.ClearStreamObserver(this);
                if (Interlocked.Exchange(ref _writerDisposeFault, null) is { } fault)
                    throw fault;
                _writer.Dispose();
            }
            catch (Exception)
            {
                // Disposal re-flushes a buffer a failed write left behind; the case has ended either way.
            }
            finally
            {
                // Always forget the case, so the terminal can start another.
                _finished(this);
                _completion.TrySetResult();
            }
        }
    }

    // A stop changes the state before a discard; offers that passed their check before it finish first, so
    // their events are discarded (and declared) with the queue rather than left unaccounted. Bounded, as a
    // producer is never held for long.
    private void WaitForOffersInFlight() =>
        SpinWait.SpinUntil(() => Volatile.Read(ref _offersInFlight) == 0, TimeSpan.FromSeconds(1));

    // Delivery records the terminal made but the writer never pulled, declared missing when the case ends
    // before it could write them.
    private void DeclareUnpulledDelivery(string reason)
    {
        if (_sources.Delivery is not { } delivery)
            return;
        var last = Math.Min(delivery.Read(long.MaxValue - 1, 1, includeBytes: false).Totals.LastSequence ?? _deliverySince,
            Volatile.Read(ref _deliveryAtStop));
        if (last <= _deliverySince)
            return;
        // Offered once known (the terminal made them), dropped once declared, as for the queue.
        var count = last - _deliverySince;
        CountDeliveryOffered(last);
        if (Interlocked.Exchange(ref _unpulledFault, null) is { } fault)
            throw fault;
        WriteMissing(new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = _deliverySince + 1, ToOrdinal = last, Reason = reason }, RangeLimit,
            () => Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], count));
        _deliverySince = last;
    }

    // Delivery records the case knows the terminal made are counted offered once, through this watermark, and
    // never withdrawn; _deliverySince moves only past records written or declared. A write or declaration that
    // fails therefore leaves offered ahead of written plus dropped (the reader's unaccounted tail), and a retry
    // that lands counts nothing twice.
    private long _deliveryOfferedThrough;

    private void CountDeliveryOffered(long through)
    {
        if (through <= _deliveryOfferedThrough)
            return;
        Interlocked.Add(ref _offered[(int)CaseStream.Delivery], through - _deliveryOfferedThrough);
        _deliveryOfferedThrough = through;
    }

    private void WriteClosingRecords()
    {
        // Whatever delivery the terminal made before the stop and the writer did not settle (evicted from the
        // ring, or behind a write still in progress at the last pull) is declared, on every stop path.
        DeclareUnpulledDelivery("not-pulled");
        WriteIntervalEndIfPending(long.MaxValue);
        WriteLoss(final: true);
        WriteStreamFailures();
        WriteRemainingCheckpoints();
        // Missing ranges that did not fit under the size bound: one range of unknown extent per stream. Their
        // counts are declared only once the summary is written; a summary that fails leaves them unaccounted.
        for (var index = 0; index < StreamCount; index++)
        {
            if (_undescribedFrom[index] is not { } from)
                continue;
            if (!AppendCase("missing", null, new DiagnosticCaseRecord
            {
                Stream = CaseArtifactWriter.StreamName((CaseStream)index),
                FromOrdinal = from,
                ToOrdinal = null,
                Reason = "size-limit-unknown-extent",
            }, ClosingLimit))
                continue;
            _undescribedFrom[index] = null;
            foreach (var declared in _deferredDeclarations[index] ?? [])
                declared();
            _deferredDeclarations[index] = null;
        }
    }

    // Writes queued events; false when the size bound stops the case.
    private bool DrainQueue()
    {
        while (Volatile.Read(ref _drainAbandoned) == 0 && _queue.TryDequeue(out var item))
        {
            // Pending until written, so a size bound or a writer failure declares it missing with the queue.
            _pendingAfterLimit = item;
            if (!Write(item))
                return false;
            _pendingAfterLimit = null;
        }

        return true;
    }

    private CaseEvent? _pendingAfterLimit;

    // Declares every event not written (the one that crossed the size bound and the queue) missing.
    private void DiscardQueued(string reason)
    {
        var ranges = new Dictionary<CaseStream, List<(long From, long To)>>();
        void Add(in CaseEvent item)
        {
            if (!ranges.TryGetValue(item.Stream, out var list))
                ranges[item.Stream] = list = [];
            if (list.Count > 0 && list[^1].To == item.Ordinal - 1)
                list[^1] = (list[^1].From, item.Ordinal);
            else
                list.Add((item.Ordinal, item.Ordinal));
        }

        if (_pendingAfterLimit is { } pending)
            Add(pending);
        _pendingAfterLimit = null;
        while (_queue.TryDequeue(out var item))
            Add(item);
        // Counted dropped only once declared: a range whose write fails leaves offered ahead of written plus
        // dropped, which the reader reports as an unaccounted tail rather than a complete stream.
        foreach (var (stream, list) in ranges)
            foreach (var (from, to) in list)
            {
                var index = (int)stream;
                WriteMissing(new DiagnosticCaseRecord { Stream = CaseArtifactWriter.StreamName(stream), FromOrdinal = from, ToOrdinal = to, Reason = reason }, RangeLimit,
                    () => Interlocked.Add(ref _dropped[index], to - from + 1));
            }
        WriteClosingRecords();
    }

    private void WriteStreamFailures()
    {
        while (_streamFailures.TryDequeue(out var failure))
            AppendCase("stream-failed", null, failure, ClosingLimit);
    }

    private long EventLimit => Manifest.Bounds.MaxBytes - EventReserve;

    private long RangeLimit => Manifest.Bounds.MaxBytes - RangeReserve;

    private long ClosingLimit => Manifest.Bounds.MaxBytes - ClosingReserve;

    // A missing range must fit below its tier. One that does not stops the case at its size bound, and its
    // stream's loss from there is summarized as one range of unknown extent when the case closes.
    // `declared` counts the range; it runs once the range is in the artifact: at once, or when the summary
    // that covers it is written.
    private void WriteMissing(DiagnosticCaseRecord record, long limit, Action declared)
    {
        var index = StreamIndex(record.Stream);
        if (_undescribedFrom[index] is null && AppendCase("missing", null, record, limit))
        {
            declared();
            return;
        }

        if (_undescribedFrom[index] is not { } from || record.FromOrdinal < from)
            _undescribedFrom[index] = record.FromOrdinal;
        (_deferredDeclarations[index] ??= []).Add(declared);
        RequestStop(DiagnosticCaseStopReason.SizeLimit);
    }

    // Per stream, the counts of ranges waiting for their summary line.
    private readonly List<Action>?[] _deferredDeclarations = new List<Action>?[StreamCount];

    // Writes one queued event; false (nothing written) when it would cross the size bound.
    private bool Write(in CaseEvent item)
    {
        if (Interlocked.Exchange(ref _writerFault, null) is { } fault)
            throw fault;
        if (item.Stream == CaseStream.Model)
            WriteIntervalEndIfPending(item.ModelSequence ?? long.MaxValue);
        return Append(item.Stream, new DiagnosticCaseEvent
        {
            Stream = CaseArtifactWriter.StreamName(item.Stream),
            Ordinal = item.Ordinal,
            Timestamp = item.Timestamp,
            Kind = item.Kind,
            ModelSequence = item.ModelSequence,
            Width = item.Width,
            Height = item.Height,
            Length = item.Length,
            Data = item.Payload is { } payload ? Convert.ToBase64String(payload) : null,
            Input = item.Detail as DiagnosticCaseInputEvent,
            Frame = item.Detail as DiagnosticCaseFrameEvent,
        }, EventLimit);
    }

    // The one append path: assigns the next case sequence and counts the line only when it was written.
    private bool Append(CaseStream stream, DiagnosticCaseEvent item, long limit)
    {
        var sequence = _caseSequence + 1;
        if (!_writer.TryWriteEvent(item with { CaseSequence = sequence }, limit))
            return false;
        _caseSequence = sequence;
        Interlocked.Increment(ref _written[(int)stream]);
        Volatile.Write(ref _lastWritten, sequence);
        return true;
    }

    // Case records are numbered by the writer alone, and only when written.
    private bool AppendCase(string kind, long? modelSequence, DiagnosticCaseRecord? record, long limit,
        DiagnosticCaseCheckpointEvent? checkpoint = null)
    {
        var ordinal = _ordinals[(int)CaseStream.Case] + 1;
        if (!Append(CaseStream.Case, new DiagnosticCaseEvent
        {
            Stream = "case",
            Ordinal = ordinal,
            Timestamp = Stopwatch.GetTimestamp(),
            Kind = kind,
            ModelSequence = modelSequence,
            Record = record,
            Checkpoint = checkpoint,
        }, limit))
            return false;
        _ordinals[(int)CaseStream.Case] = ordinal;
        return true;
    }

    // Writes up to `count` pending marks, oldest first; false when one could not be written at all (the case
    // is then at its size bound, and the rest are written or declared as it closes).
    private bool WriteCheckpoints(int count, long limit)
    {
        for (var i = 0; i < count && _checkpoints.TryPeek(out var pending); i++)
        {
            if (!WriteCheckpoint(pending, limit))
                return false;
            _checkpoints.TryDequeue(out _);
            ReleaseMarkSlot();
            ReleaseStateBytes(pending.StateBytes);
        }
        return true;
    }

    // A checkpoint whose state does not fit is written without it (status missing, reason size-limit), so the
    // boundary stays in the artifact; false when not even that fits.
    private bool WriteCheckpoint(in PendingCheckpoint pending, long limit)
    {
        var written = AppendCase("checkpoint", pending.ModelSequence, null, limit, pending.Checkpoint)
            || (pending.Checkpoint.State is not null
                && AppendCase("checkpoint", pending.ModelSequence, null, limit,
                    pending.Checkpoint with { Status = "missing", Reason = "size-limit", CaptureMilliseconds = null, State = null }));
        _writer.TrimBuffer();
        if (written)
            Interlocked.Increment(ref _checkpointsWritten);
        return written;
    }

    // As the case closes: every mark still pending, then the stop checkpoint. Those that cannot be written at
    // all are declared missing as ranges of checkpoint ordinals; a range that fails leaves them unaccounted.
    private void WriteRemainingCheckpoints()
    {
        // A mark that passed its in-lock check before the stop enqueues shortly; bounded, as a mark holds the
        // model lock only while it takes its state.
        SpinWait.SpinUntil(() => Volatile.Read(ref _marksInProgress) == 0, TimeSpan.FromSeconds(1));
        var unwritten = new List<long>();
        while (_checkpoints.TryDequeue(out var pending))
        {
            if (!WriteCheckpoint(pending, RangeLimit))
                unwritten.Add(pending.Checkpoint.Ordinal);
            ReleaseMarkSlot();
            ReleaseStateBytes(pending.StateBytes);
        }

        if (!_stopCheckpointSettled && _stopCheckpoint is { } stop)
        {
            _stopCheckpointSettled = true;
            if (!WriteCheckpoint(stop, RangeLimit))
                unwritten.Add(stop.Checkpoint.Ordinal);
            ReleaseStateBytes(stop.StateBytes);
        }

        unwritten.Sort();
        for (var start = 0; start < unwritten.Count;)
        {
            var end = start;
            while (end + 1 < unwritten.Count && unwritten[end + 1] == unwritten[end] + 1)
                end++;
            var from = unwritten[start];
            var to = unwritten[end];
            if (AppendCase("missing", null, new DiagnosticCaseRecord { Stream = "checkpoint", FromOrdinal = from, ToOrdinal = to, Reason = "size-limit" },
                    ClosingLimit))
                Interlocked.Add(ref _checkpointsDropped, to - from + 1);
            start = end + 1;
        }
    }

    // Writes the interval end before the first model event at or after it (or at the end of the case).
    private void WriteIntervalEndIfPending(long nextModelSequence)
    {
        var end = Volatile.Read(ref _intervalEnd);
        if (_intervalEndWritten || end == long.MaxValue || nextModelSequence < end)
            return;
        _intervalEndWritten = true;
        AppendCase("interval-end", end, new DiagnosticCaseRecord { Stream = "model", Reason = _intervalEndReason ?? "unsupported" }, ClosingLimit);
    }

    // Ranges taken from the ledger stay pending until written, so a writer failure partway through keeps the
    // rest for the failure path to declare.
    private readonly Queue<DiagnosticCaseRecord> _pendingLoss = new();

    // Overload drops per stream whose ranges are not declared yet; the completion reports only declared loss,
    // so a range that storage never accepted leaves an unaccounted tail. Once a stream's loss of unknown extent
    // is declared, it covers every later drop.
    private readonly long[] _undeclaredOverload = new long[StreamCount];
    private readonly bool[] _overloadUnknownDeclared = new bool[StreamCount];

    private void WriteLoss(bool final)
    {
        foreach (var record in _loss.Take(final))
            _pendingLoss.Enqueue(record);
        while (_pendingLoss.TryPeek(out var record))
        {
            if (Interlocked.Exchange(ref _lossFault, null) is { } fault)
                throw fault;
            if (_persistentLossFault is { } persistent)
                throw persistent;
            var index = StreamIndex(record.Stream);
            WriteMissing(record, final ? RangeLimit : EventLimit, () =>
            {
                if (record.ToOrdinal is { } to)
                {
                    Interlocked.Add(ref _undeclaredOverload[index], -(to - (record.FromOrdinal ?? to) + 1));
                }
                else
                {
                    Volatile.Write(ref _overloadUnknownDeclared[index], true);
                    Interlocked.Exchange(ref _undeclaredOverload[index], 0);
                }
            });
            _pendingLoss.Dequeue();
        }
    }

    // Native delivery records live in their own bounded ring; the writer pulls them instead of adding a
    // hook to the presentation write path. Records evicted between pulls become a missing range.
    // Writes delivery records pulled since the last pass; false when the size bound stops the case.
    private bool PullDelivery()
    {
        if (_sources.Delivery is not { } delivery)
            return true;
        var snapshot = delivery.Read(_deliverySince, NativeDeliveryRecorder.MaxRecords, _nativeOutput);
        var stop = Volatile.Read(ref _deliveryAtStop);
        foreach (var record in snapshot.Records)
        {
            // Records evicted before this pull: declared up to the stop, and settled, before anything else.
            if (record.Sequence > _deliverySince + 1 && _deliverySince < stop)
            {
                var to = Math.Min(record.Sequence - 1, stop);
                var missing = to - _deliverySince;
                CountDeliveryOffered(to);
                WriteMissing(new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = _deliverySince + 1, ToOrdinal = to, Reason = "evicted" },
                    EventLimit, () => Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], missing));
                _deliverySince = to;
            }

            // Records the terminal made after the case stopped are not the case's, like any other late offer.
            if (record.Sequence > stop)
                break;

            // Offered before the write, so status never shows more written than offered.
            CountDeliveryOffered(record.Sequence);
            if (Interlocked.Exchange(ref _deliveryFault, null) is { } fault)
                throw fault;
            if (!Append(CaseStream.Delivery, new DiagnosticCaseEvent
            {
                Stream = "delivery",
                Ordinal = record.Sequence,
                Timestamp = record.StartTimestamp,
                Kind = "delivery",
                Delivery = record,
            }, EventLimit))
            {
                // This record and the rest of the snapshot, up to the stop, were pulled but never written.
                var last = Math.Min(snapshot.Records[^1].Sequence, stop);
                var count = last - record.Sequence + 1;
                CountDeliveryOffered(last);
                WriteMissing(new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = record.Sequence, ToOrdinal = last, Reason = "size-limit" },
                    RangeLimit, () => Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], count));
                _deliverySince = last;
                return false;
            }

            _deliverySince = record.Sequence;
        }

        return true;
    }

    private DiagnosticCaseCompletion DescribeCompletion() => new()
    {
        CaseId = CaseId,
        StopReason = (DiagnosticCaseStopReason)Volatile.Read(ref _stopReason),
        StoppedAt = _stoppedAt ?? _timeProvider.GetUtcNow(),
        LastCaseSequence = Volatile.Read(ref _lastWritten) is var last and >= 0 ? last : null,
        BytesWritten = _writer.BytesWritten,
        Streams = DescribeStreams(declaredOnly: true),
        Checkpoints = DescribeCheckpoints(),
    };

    private DiagnosticCaseStreamStatus DescribeCheckpoints() => new()
    {
        Stream = "checkpoint",
        Offered = Interlocked.Read(ref _checkpointsTaken),
        Written = Interlocked.Read(ref _checkpointsWritten),
        Dropped = Interlocked.Read(ref _checkpointsDropped),
    };

    // Status reports every loss as it happens; the completion only loss whose range the artifact declares.
    private IReadOnlyList<DiagnosticCaseStreamStatus> DescribeStreams(bool declaredOnly = false) =>
        Manifest.Streams.Where(s => s.Events == DiagnosticCoverageState.Included).Select(s => (Declaration: s, Index: StreamIndex(s.Stream)))
            .Select(s => new DiagnosticCaseStreamStatus
            {
                Stream = s.Declaration.Stream,
                Offered = Interlocked.Read(ref _offered[s.Index]),
                Written = Interlocked.Read(ref _written[s.Index]),
                // Once a stream's loss of unknown extent is declared it covers every later drop, including one that
                // counted itself undeclared while the writer was declaring it.
                Dropped = Interlocked.Read(ref _dropped[s.Index]) - (declaredOnly && !Volatile.Read(ref _overloadUnknownDeclared[s.Index])
                    ? Math.Max(0, Interlocked.Read(ref _undeclaredOverload[s.Index])) : 0),
            }).ToList();

    private static int StreamIndex(string name) => name switch
    {
        "model" => (int)CaseStream.Model,
        "input" => (int)CaseStream.Input,
        "frames" => (int)CaseStream.Frames,
        "delivery" => (int)CaseStream.Delivery,
        _ => (int)CaseStream.Case,
    };

    /// <summary>The case as a start, stop or status result.</summary>
    internal DiagnosticCaseResult Describe()
    {
        var state = Volatile.Read(ref _state);
        var reason = Volatile.Read(ref _stopReason);
        var end = state == Stopped ? _stoppedAt : null;
        return new DiagnosticCaseResult
        {
            Outcome = DiagnosticOutcome.Captured,
            CaseId = CaseId,
            Path = Path,
            State = state switch { Recording => DiagnosticCaseState.Recording, Stopping => DiagnosticCaseState.Stopping, _ => DiagnosticCaseState.Stopped },
            StartPath = Manifest.StartPath,
            Bounds = Manifest.Bounds,
            Authorizations = Manifest.Authorizations,
            Checkpoint = Manifest.Checkpoint,
            StartedAt = Manifest.StartedAt,
            ElapsedSeconds = ((end ?? _timeProvider.GetUtcNow()) - Manifest.StartedAt).TotalSeconds,
            BytesWritten = _writer.BytesWritten,
            Streams = DescribeStreams(),
            Checkpoints = DescribeCheckpoints(),
            StopReason = state == Recording || reason < 0 ? null : (DiagnosticCaseStopReason)reason,
        };
    }
}
