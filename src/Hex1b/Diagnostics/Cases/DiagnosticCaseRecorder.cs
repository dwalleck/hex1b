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

    /// <summary>An exception the writer's disposal throws, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> WriterDisposeFaultForTesting = new();

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
        }
    }

    /// <summary>Asks the case to stop without waiting; the first reason wins.</summary>
    internal void RequestStop(DiagnosticCaseStopReason reason)
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
                if (!drained || !PullDelivery())
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
        // Offered first (the terminal made them), dropped once declared, as for the queue.
        var count = last - _deliverySince;
        Interlocked.Add(ref _offered[(int)CaseStream.Delivery], count);
        WriteMissing(new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = _deliverySince + 1, ToOrdinal = last, Reason = reason }, RangeLimit,
            () => Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], count));
        _deliverySince = last;
    }

    private void WriteClosingRecords()
    {
        // Whatever delivery the terminal made before the stop and the writer did not settle (evicted from the
        // ring, or behind a write still in progress at the last pull) is declared, on every stop path.
        DeclareUnpulledDelivery("not-pulled");
        WriteIntervalEndIfPending(long.MaxValue);
        WriteLoss(final: true);
        WriteStreamFailures();
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
    private bool AppendCase(string kind, long? modelSequence, DiagnosticCaseRecord record, long limit)
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
        }, limit))
            return false;
        _ordinals[(int)CaseStream.Case] = ordinal;
        return true;
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
            // Counts follow what was declared or written, and _deliverySince only what is settled, so a write
            // that throws leaves nothing counted twice when the failure path declares the rest.
            if (record.Sequence > _deliverySince + 1 && _deliverySince < stop)
            {
                var to = Math.Min(record.Sequence - 1, stop);
                var missing = to - _deliverySince;
                WriteMissing(new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = _deliverySince + 1, ToOrdinal = to, Reason = "evicted" },
                    EventLimit, () =>
                    {
                        Interlocked.Add(ref _offered[(int)CaseStream.Delivery], missing);
                        Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], missing);
                    });
                _deliverySince = to;
            }

            // Records the terminal made after the case stopped are not the case's, like any other late offer.
            if (record.Sequence > stop)
                break;

            // Offered before the write, so status never shows more written than offered; withdrawn when the
            // record is not written after all.
            Interlocked.Increment(ref _offered[(int)CaseStream.Delivery]);
            bool appended;
            try
            {
                if (Interlocked.Exchange(ref _deliveryFault, null) is { } fault)
                    throw fault;
                appended = Append(CaseStream.Delivery, new DiagnosticCaseEvent
                {
                    Stream = "delivery",
                    Ordinal = record.Sequence,
                    Timestamp = record.StartTimestamp,
                    Kind = "delivery",
                    Delivery = record,
                }, EventLimit);
            }
            catch
            {
                Interlocked.Decrement(ref _offered[(int)CaseStream.Delivery]);
                throw;
            }

            if (!appended)
            {
                // This record and the rest of the snapshot, up to the stop, were pulled but never written.
                Interlocked.Decrement(ref _offered[(int)CaseStream.Delivery]);
                var last = Math.Min(snapshot.Records[^1].Sequence, stop);
                var count = last - record.Sequence + 1;
                WriteMissing(new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = record.Sequence, ToOrdinal = last, Reason = "size-limit" },
                    RangeLimit, () =>
                    {
                        Interlocked.Add(ref _offered[(int)CaseStream.Delivery], count);
                        Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], count);
                    });
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
            StopReason = state == Recording || reason < 0 ? null : (DiagnosticCaseStopReason)reason,
        };
    }
}
